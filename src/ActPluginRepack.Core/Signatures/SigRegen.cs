using System.Text;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

// 시그니처 자동 복구: 옛 exe 에서는 맞고 새 exe 에서는 깨진 시그니처를, 두 exe 를 비교해 새로 만든다.
//
// 패치로 시그니처가 깨지는 흔한 이유는 "같은 일을 하는 코드인데 레지스터 배정이 바뀜"이다 (deucalion 이력의
// 7.30 / 7.30h / 7.31h / 7.57 수정이 그랬다). 함수 전체는 꽤 바뀌어도(7.57 은 수신 경로가 재구성됨)
// 시그니처가 덮는 명령어 몇 개의 "모양"(레지스터·주소를 뺀 명령어)은 그대로인 경우가 많다. 그래서:
//   1) 위치 찾기 — 옛 패턴이 덮던 명령어들의 모양을 새 exe 전체에서 찾는다 (옛 패턴이 와일드카드로 둔 값은
//      모양에서도 뺀다). 안 되면 앞부분만으로 줄여서, call 형이면 대상 함수가 대응하는 것으로 거른다.
//      후보가 여럿이면 앞뒤 문맥이 가장 비슷한 곳. 그래도 없으면 call 대상/감싼 함수 대응으로.
//   2) 새 패턴 — 새 위치의 실제 명령어로 만든다. 주소성 필드(RIP 변위, 분기 대상)와 옛 패턴이 와일드카드였던
//      필드는 와일드카드, call 은 "E8 $ { ' }". 옛 패턴이 명령어 중간(변위/즉치 직전)에서 끝났으면 새 명령어의
//      같은 필드 앞에서 끝낸다 — 플러그인이 패턴 바로 뒤의 값을 읽기 때문.
//   3) 검증 — 새 exe 에서 정확히 그 위치들에만 맞아야 하고(옛 exe 의 일치 개수와 같음), call 형은 대상 함수도
//      대응해야 한다. 넓으면 뒤 명령어로 늘린다. 통과 못 하면 실패로 보고한다 (틀린 시그니처를 만들지 않음).
internal static class SigRegen
{
    public sealed record Result(string? Pattern, string Detail);

    private const int MaxMatches = 16;

    public static Result Regenerate(CodeImage oldExe, CodeImage newExe, string oldPattern)
    {
        Slot[] slots;
        try { slots = Slot.Parse(oldPattern); }
        catch (ToolException ex) { return new Result(null, ex.Message); }

        var sig = SigPattern.Parse(oldPattern);
        var oldMatches = sig.Scan(oldExe.Target);
        if (oldMatches.Count == 0) return new Result(null, "옛 exe 에서도 맞지 않음");
        // 위치마다 전체 exe 를 훑으므로 너무 흔한 패턴은 다루지 않는다 (시간·메모리, 그리고 그런 패턴은 위치 대응이 모호)
        if (oldMatches.Count > MaxMatches) return new Result(null, $"옛 exe 에서 {oldMatches.Count}곳에 맞음 - 자동 복구는 {MaxMatches}곳까지만");
        if (sig.Scan(newExe.Target).Count == oldMatches.Count) return new Result(null, "새 exe 에서도 이미 맞음");

        // 패턴이 끝난 직후의 필드 값(패턴 밖)도 처음엔 위치를 가리는 단서로 쓰고, 못 찾으면 빼고 다시
        var strict = Regenerate(oldExe, newExe, slots, oldMatches, looseCut: false, out bool hasCut);
        if (strict.Pattern != null || !hasCut) return strict;
        var loose = Regenerate(oldExe, newExe, slots, oldMatches, looseCut: true, out _);
        return loose.Pattern != null ? loose with { Detail = loose.Detail + ", 패턴 밖 값 무시" } : strict;
    }

    private static Result Regenerate(CodeImage oldExe, CodeImage newExe, Slot[] slots, List<SigPattern.Match> oldMatches,
                                     bool looseCut, out bool hasCut)
    {
        int jumpSlot = Array.FindIndex(slots, s => s.Kind == SlotKind.Jump);
        bool isJump = jumpSlot >= 0;
        var layout = Layout(oldExe, oldMatches[0].Rva, slots, looseCut); // 옛 패턴이 덮는 명령어 구성 (첫 일치 기준)
        hasCut = layout.Any(p => p.CutAtDisp || p.CutAtImm);

        // ---- 1) 위치 찾기 ----
        var sites = new List<int>();
        var targets = new List<int?>();
        var mappedTargets = isJump ? oldMatches.Select(m => newExe.MapFunctionStart(oldExe, m.Saves[0])).ToList() : null;
        // 전체 모양이 새 exe 에서 옛 일치 수와 똑같은 개수만큼만 나오면, 위치마다 짝을 짓지 않고 그대로 쓴다
        // (어차피 그 위치들 전체로 패턴을 만들고 검증한다 — 7.57 RECV 처럼 주변 코드가 많이 바뀐 경우)
        var whole = newExe.FindShape(QueryParts(layout).Select(p => (Func<Instruction, bool>)(ins => SameShape(p, ins))).ToList());
        Debug($"query: {string.Join(" ; ", QueryParts(layout).Select(p => Normalize(p.Ins, p.WildImm, p.WildDisp)))}");
        Debug($"whole-shape candidates: {whole.Count} ({string.Join(",", whole.Take(10).Select(c => c.Rva.ToString("x")))})");
        var group = whole.Count >= oldMatches.Count ? PickGroup(oldExe, newExe, oldMatches, slots, layout, whole) : null;
        if (group != null && mappedTargets != null)
        {
            // call 형: 대상 함수가 대응된 것이 있으면 그 대상들이 묶음의 call 대상에 들어 있어야 한다
            var known = mappedTargets.Where(t => t != null).Select(t => t!.Value).ToHashSet();
            var got = group.Select(newExe.CallTarget).ToHashSet();
            Debug($"group call targets: {string.Join(",", got.Select(t => t?.ToString("x")))} / mapped: {string.Join(",", known.Select(t => t.ToString("x")))}");
            if (known.Count > 0 && !known.All(t => got.Contains(t))) group = null;
        }
        if (group != null)
        {
            sites.AddRange(group);
            targets.AddRange(sites.Select(_ => (int?)null));
        }
        else
        {
            var used = new HashSet<int>();
            for (int i = 0; i < oldMatches.Count; i++)
            {
                var m = oldMatches[i];
                int? newTarget = mappedTargets?[i];
                int? site = FindSite(oldExe, newExe, m.Rva, layout, isJump, newTarget, used, out _);
                if (site == null) return new Result(null, $"0x{m.Rva:x} 에 대응하는 위치를 새 exe 에서 못 찾음");
                used.Add(site.Value);
                sites.Add(site.Value);
                targets.Add(newTarget);
            }
        }

        // ---- 2) 새 패턴, 3) 검증 ----
        // 옛 패턴이 명령어 중간에서 끝났으면(플러그인 메모리 시그니처처럼 패턴 바로 뒤 값을 읽음) 끝 위치를 지켜야 하므로
        // 넓을 때 뒤가 아니라 앞 명령어로 늘린다. 시작 위치가 중요한 후크 시그니처는 뒤로 늘린다.
        bool keepEnd = !isJump && layout.Count > 0 && layout[^1].Partial;
        string? last = null;
        for (int extra = 0; extra <= 8; extra++)
        {
            int pre = keepEnd ? extra : 0;
            var starts = sites.Select(s => pre == 0 ? s : newExe.Before(s, pre)).ToList();
            if (starts.Any(s => s == null)) break;
            var perSite = sites.Select(s => Build(newExe, s, layout, extra, keepEnd)).ToList();
            if (perSite.Any(p => p == null)) return new Result(null, "새 위치에서 명령어를 읽지 못함");
            var merged = Merge(perSite!);
            if (merged == null) return new Result(null, "위치마다 명령어 구성이 다름");
            last = string.Join(" ", merged);
            var matches = SigPattern.Parse(last).Scan(newExe.Target);
            if (matches.Count > oldMatches.Count) continue; // 너무 넓음 -> 뒤 명령어로 늘림
            if (!matches.Select(x => x.Rva).Order().SequenceEqual(starts.Select(s => s!.Value).Order()))
                return new Result(null, $"새 패턴이 대응 위치와 다르게 맞음 ({matches.Count}곳): {last}");
            if (isJump)
            {
                if (matches.Any(x => x.Saves.Count == 0)) return new Result(null, $"call 형인데 새 패턴에 call 이 없음: {last}");
                var got = matches.OrderBy(x => x.Rva).Select(x => x.Saves[0]).ToList();
                var want = sites.Zip(targets).OrderBy(x => x.First).Select(x => x.Second).ToList();
                if (got.Zip(want).Any(p => p.Second != null && p.First != p.Second))
                    return new Result(null, $"새 패턴의 call 대상이 옛 대상과 대응하지 않음: {last}");
            }
            return new Result(last, $"{matches.Count}곳" + (extra > 0 ? $", {(keepEnd ? "앞" : "뒤")} 명령어 {extra}개 늘림" : ""));
        }
        return new Result(null, $"새 패턴이 너무 넓음: {last}");
    }

    private static readonly bool DebugOn = Environment.GetEnvironmentVariable("ACTREPACK_DEBUG") == "1";
    private static void Debug(string s) { if (DebugOn) Log.Note("  [sigregen] " + s); }

    // ---- 옛 패턴이 덮는 명령어 구성 ----

    // 명령어 하나에 대해: 덮은 바이트 수, 옛 패턴이 와일드카드였던 필드, call 점프 여부, 1바이트 대안
    private sealed record Part(Instruction Ins, int Covered, bool WildImm, bool WildDisp, bool Jump, Dictionary<int, Slot> Alts,
                               bool CutAtDisp, bool CutAtImm)
    {
        public bool Partial => Covered < Ins.Length;
    }

    private static List<Part> Layout(CodeImage exe, int rva, Slot[] slots, bool looseCut)
    {
        var parts = new List<Part>();
        int pos = 0;
        foreach (var ins in exe.DecodeAt(rva, slots.Length))
        {
            if (pos >= slots.Length) break;
            var off = exe.ConstantOffsets(ins);
            int covered = Math.Min(ins.Length, slots.Length - pos);
            var s = slots.Skip(pos).Take(covered).ToArray();
            bool Wild(int o, int size) => size > 0 && Enumerable.Range(o, size).Any(i => i < s.Length && s[i].Kind == SlotKind.Wild);
            var alts = s.Select((x, i) => (x, i)).Where(t => t.x.Kind == SlotKind.Alt).ToDictionary(t => t.i, t => t.x);
            // looseCut: 패턴이 덮지 않은 필드(그 직전에서 끊김)는 값이 바뀌어도 되므로 와일드카드와 같이 본다
            bool cutDisp = looseCut && covered < ins.Length && off.DisplacementSize > 0 && covered <= off.DisplacementOffset;
            bool cutImm = looseCut && covered < ins.Length && off.ImmediateSize > 0 && covered <= off.ImmediateOffset;
            parts.Add(new Part(ins, covered,
                WildImm: Wild(off.ImmediateOffset, off.ImmediateSize) || cutImm,
                WildDisp: Wild(off.DisplacementOffset, off.DisplacementSize) || cutDisp,
                Jump: s.Length >= 2 && s[0].Kind == SlotKind.Fixed && s[0].Value == 0xE8 && s[1].Kind == SlotKind.Jump,
                Alts: alts,
                CutAtDisp: covered < ins.Length && off.DisplacementSize > 0 && covered == off.DisplacementOffset,
                CutAtImm: covered < ins.Length && off.ImmediateSize > 0 && covered == off.ImmediateOffset));
            pos += ins.Length;
        }
        return parts;
    }

    // 모양 검색에 쓰는 명령어들: 다 덮은 명령어 + 필드 경계에서 끊긴 마지막 명령어 (그 밖의 부분 명령어는 제외)
    private static List<Part> QueryParts(List<Part> layout) =>
        layout.Where(p => !p.Partial || p.CutAtDisp || p.CutAtImm).ToList();

    // ---- 위치 찾기 ----

    private static int? FindSite(CodeImage oldExe, CodeImage newExe, int oldRva, List<Part> layout, bool isJump,
                                 int? newTarget, HashSet<int> used, out string how)
    {
        var query = QueryParts(layout);
        var oldPos = oldExe.Position(oldRva);
        // 앞부분이 최소 2개 명령어(또는 전부)가 남을 때까지 줄여 가며 모양 검색
        for (int n = query.Count; n >= Math.Min(2, query.Count); n--)
        {
            var q = query.Take(n).ToList();
            var cands = newExe.FindShape(q.Select(p => (Func<Instruction, bool>)(ins => SameShape(p, ins))).ToList())
                .Where(c => !used.Contains(c.Rva))
                .Where(c => !isJump || newTarget == null || newExe.CallTarget(c.Rva) == newTarget)
                .ToList();
            if (cands.Count == 0) continue;
            if (cands.Count == 1) { how = $"모양 {n}개"; return cands[0].Rva; }
            if (oldPos == null) continue;
            var scored = cands.Select(c => (c.Rva, Score: newExe.ContextScore(oldExe, oldPos.Value, c.Pos)))
                .OrderByDescending(x => x.Score).ToList();
            if (scored[0].Score > scored[1].Score) { how = $"모양 {n}개 + 문맥"; return scored[0].Rva; }
        }
        if (isJump && newTarget != null && newExe.BestCaller(oldExe, oldRva, newTarget.Value) is { } call && !used.Contains(call))
        { how = "call 대상"; return call; }
        how = "함수 대응";
        return newExe.MapAddress(oldExe, oldRva);
    }

    // 모양 후보들을 "만들어지는 새 패턴"이 같은 것끼리 묶는다. 크기가 옛 일치 수와 같고 그 패턴이 정확히 그 묶음에만
    // 맞는 묶음이 해답 후보. 여럿이면 옛 바이트(옛 패턴이 덮던 고정 바이트, 끝의 부분 명령어 포함)와 더 비슷하고
    // 앞뒤 문맥이 더 비슷한 쪽. 7.57 RECV: 같은 모양 call 이 6곳(정답 3 + 다른 3), 옛 패턴 끝의 REX 바이트가 가른다.
    private static List<int>? PickGroup(CodeImage oldExe, CodeImage newExe, List<SigPattern.Match> oldMatches, Slot[] slots,
                                        List<Part> layout, List<(int Rva, (int Func, int Index) Pos)> cands)
    {
        var groups = cands
            .Select(c => (c.Rva, c.Pos, Tokens: Build(newExe, c.Rva, layout, 0, false)))
            .Where(c => c.Tokens != null)
            .GroupBy(c => string.Join(" ", c.Tokens!))
            .Where(g => g.Count() == oldMatches.Count)
            .Where(g => SigPattern.Parse(g.Key).Scan(newExe.Target).Select(m => m.Rva).Order()
                        .SequenceEqual(g.Select(c => c.Rva).Order()))
            .ToList();
        Debug($"groups: {string.Join(" | ", groups.Select(g => $"{g.Key} @ {string.Join(",", g.Select(c => c.Rva.ToString("x")))}"))}");
        if (groups.Count == 0) return null;
        if (groups.Count == 1) return groups[0].Select(c => c.Rva).ToList();
        // 여럿인데 call 형이 아니면(예: 함수 시작부 — 같은 모양이 흔함) 바이트 유사도로 고르면 위험하다.
        // 위치마다 함수 문맥으로 찾는 쪽에 맡긴다.
        if (!layout.Any(p => p.Jump)) return null;

        var oldPos = oldMatches.Select(m => oldExe.Position(m.Rva)).ToList();
        var scored = groups.Select(g =>
        {
            int bytes = g.Sum(c => oldMatches.Max(m => ByteSimilarity(oldExe, m.Rva, newExe, c.Rva, slots)));
            int context = g.Sum(c => oldPos.Where(p => p != null).Select(p => newExe.ContextScore(oldExe, p!.Value, c.Pos)).DefaultIfEmpty(0).Max());
            return (Sites: g.Select(c => c.Rva).ToList(), Score: (bytes, context));
        }).OrderByDescending(x => x.Score).ToList();
        Debug($"group scores: {string.Join(" | ", scored.Select(s => $"{s.Score}"))}");
        return scored[0].Score.CompareTo(scored[1].Score) > 0 ? scored[0].Sites : null;
    }

    // 옛 패턴의 고정 바이트 자리에서 옛/새 바이트가 같은 개수
    private static int ByteSimilarity(CodeImage oldExe, int oldRva, CodeImage newExe, int newRva, Slot[] slots)
    {
        var a = oldExe.Target.Bytes; var b = newExe.Target.Bytes;
        int ao = oldExe.FileOffset(oldRva), bo = newExe.FileOffset(newRva);
        int same = 0;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].Kind == SlotKind.Fixed && ao + i < a.Length && bo + i < b.Length && a[ao + i] == b[bo + i]) same++;
        return same;
    }

    // 옛 명령어(와일드카드 필드 제외)와 새 명령어의 모양이 같은지
    // 명령어 종류·피연산자 수가 다르면 문자열을 만들 것도 없이 다르다 (exe 전체를 훑으므로 대부분 여기서 걸러진다)
    private static bool SameShape(Part p, in Instruction ins) =>
        p.Ins.Mnemonic == ins.Mnemonic && p.Ins.OpCount == ins.OpCount &&
        Normalize(p.Ins, p.WildImm, p.WildDisp) == Normalize(ins, p.WildImm, p.WildDisp);

    // ---- 새 패턴 ----

    private static List<string>? Build(CodeImage exe, int rva, List<Part> layout, int extra, bool keepEnd)
    {
        bool isJump = layout.Any(p => p.Jump);
        int pre = keepEnd ? extra : 0, post = keepEnd ? 0 : extra;
        int start = pre == 0 ? rva : exe.Before(rva, pre) ?? -1;
        if (start < 0) return null;
        var ins = exe.DecodeAt(start, 0, pre + layout.Count + post);
        if (ins.Count < pre + layout.Count) return null;
        var tokens = new List<string>();
        for (int k = 0; k < ins.Count; k++)
        {
            var ni = ins[k];
            var p = k >= pre && k - pre < layout.Count ? layout[k - pre] : null;
            byte[] nb = exe.Bytes(ni);
            var off = exe.ConstantOffsets(ni);
            bool same = p != null && SameShape(p, ni);

            if (p != null && p.Jump && ni.FlowControl == FlowControl.Call && nb[0] == 0xE8)
            {
                tokens.Add("E8"); tokens.Add("$ { ' }");
                continue;
            }
            int take = nb.Length;
            if (p != null && p.Partial && post == 0)
            {
                // 옛 패턴이 필드 경계에서 끝났으면 새 명령어의 같은 필드 앞에서 끝낸다
                if (same && p.CutAtDisp && off.DisplacementSize > 0) take = off.DisplacementOffset;
                else if (same && p.CutAtImm && off.ImmediateSize > 0) take = off.ImmediateOffset;
                else if (isJump) break;                                  // call 형: 남는 부분 명령어는 뺀다
                else if (same && ni.Length == p.Ins.Length) take = p.Covered;
                else return null;
            }
            for (int b = 0; b < take; b++)
            {
                bool inDisp = off.DisplacementSize > 0 && b >= off.DisplacementOffset && b < off.DisplacementOffset + off.DisplacementSize;
                bool inImm = off.ImmediateSize > 0 && b >= off.ImmediateOffset && b < off.ImmediateOffset + off.ImmediateSize;
                bool addressLike = (inDisp && ni.IsIPRelativeMemoryOperand) || (inImm && IsBranch(ni));
                bool oldWild = same && ((inDisp && p!.WildDisp) || (inImm && p!.WildImm));
                Slot? alt = same && ni.Length == p!.Ins.Length && p.Alts.TryGetValue(b, out var a) ? a : null;
                tokens.Add(addressLike || oldWild ? "?"
                         : alt != null && alt.Values!.Contains(nb[b]) ? alt.Text
                         : nb[b].ToString("X2"));
            }
            if (take < nb.Length) break;
        }
        return tokens;
    }

    private static bool IsBranch(in Instruction i) =>
        i.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch;

    // 위치별 토큰을 합친다: 같으면 그대로, 다르면 와일드카드 (길이·구조가 다르면 실패)
    private static List<string>? Merge(List<List<string>> all)
    {
        if (all.Select(t => t.Count).Distinct().Count() != 1) return null;
        var merged = new List<string>();
        for (int i = 0; i < all[0].Count; i++)
        {
            var column = all.Select(t => t[i]).Distinct().ToList();
            if (column.Count == 1) merged.Add(column[0]);
            else if (column.Any(c => c.StartsWith('$') || c.StartsWith('('))) return null;
            else merged.Add("?");
        }
        return merged;
    }

    // 레지스터와 주소성 값을 뺀 명령어 모양. dropImm/dropDisp 면 즉치/변위 값도 뺀다.
    public static string Normalize(in Instruction i, bool dropImm = false, bool dropDisp = false)
    {
        var sb = new StringBuilder();
        sb.Append(i.Mnemonic);
        for (int k = 0; k < i.OpCount; k++)
        {
            sb.Append(k == 0 ? ' ' : ',');
            switch (i.GetOpKind(k))
            {
                case OpKind.Register:
                    var r = i.GetOpRegister(k);
                    sb.Append(r.IsGPR() ? $"r{r.GetSize()}" : r.IsXMM() ? "x" : r.IsYMM() ? "y" : r.ToString());
                    break;
                case OpKind.Memory:
                    sb.Append('m').Append(i.MemorySize);
                    // 구조체 오프셋은 남기고(대개 안 바뀜) 스택/RIP 변위는 뺀다
                    if (!dropDisp && !i.IsIPRelativeMemoryOperand && i.MemoryBase != Register.None
                        && i.MemoryBase is not (Register.RSP or Register.RBP or Register.ESP))
                        sb.Append('+').Append(i.MemoryDisplacement64.ToString("x"));
                    break;
                case OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64:
                    sb.Append('b');
                    break;
                case OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64
                    or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64:
                    sb.Append('#');
                    if (!dropImm) sb.Append(i.GetImmediate(k).ToString("x"));
                    break;
                default:
                    sb.Append(i.GetOpKind(k));
                    break;
            }
        }
        return sb.ToString();
    }

    // ---- 옛 패턴의 바이트 칸 ----

    private enum SlotKind { Fixed, Wild, Alt, Jump }

    private sealed record Slot(SlotKind Kind, byte Value = 0, byte[]? Values = null, string Text = "")
    {
        // 바이트 칸 단위로 푼다. $ { ' } 는 4칸(Jump). 여러 바이트짜리 대안과 [n-m] 은 미지원.
        public static Slot[] Parse(string s)
        {
            var result = new List<Slot>();
            for (int i = 0; i < s.Length; )
            {
                char c = s[i];
                if (c == ' ') { i++; continue; }
                // $ { } 밖의 북마크는 deucalion 이 후킹 위치로 쓰는데, 다시 만든 패턴에서 위치를 옮길 수 없다
                if (c == '\'') throw new ToolException("자동 복구는 $ { } 밖의 북마크(')를 지원하지 않습니다.");
                if (Uri.IsHexDigit(c) && i + 1 < s.Length && Uri.IsHexDigit(s[i + 1]))
                { result.Add(new Slot(SlotKind.Fixed, Convert.ToByte(s.Substring(i, 2), 16))); i += 2; continue; }
                if (c == '?') { result.Add(new Slot(SlotKind.Wild)); i++; continue; }
                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*') { result.Add(new Slot(SlotKind.Wild)); i += 2; continue; }
                if (c == '$')
                {
                    int close = s.IndexOf('}', i);
                    if (close < 0) throw new ToolException("패턴의 $ { } 형식이 잘못되었습니다.");
                    for (int k = 0; k < 4; k++) result.Add(new Slot(SlotKind.Jump));
                    i = close + 1;
                    continue;
                }
                if (c == '(')
                {
                    int close = s.IndexOf(')', i);
                    if (close < 0) throw new ToolException("패턴의 ( ) 형식이 잘못되었습니다.");
                    var parts = s[(i + 1)..close].Split('|', StringSplitOptions.TrimEntries);
                    if (parts.Any(p => p.Length != 2 || !Uri.IsHexDigit(p[0]) || !Uri.IsHexDigit(p[1])))
                        throw new ToolException("자동 복구는 1바이트 고정값 대안만 지원합니다.");
                    result.Add(new Slot(SlotKind.Alt, Values: parts.Select(p => Convert.ToByte(p, 16)).ToArray(), Text: s[i..(close + 1)]));
                    i = close + 1;
                    continue;
                }
                throw new ToolException($"자동 복구가 지원하지 않는 패턴 토큰입니다: '{c}'");
            }
            return result.ToArray();
        }
    }
}

// 디스어셈블과 함수/위치 대응을 위한 exe 표현 (.text + .pdata)
internal sealed class CodeImage
{
    public ScanTarget Target { get; }
    private readonly PeImage _pe;
    private readonly ulong _imageBase;
    private readonly (int Begin, int End)[] _functions; // RVA, 시작 주소 정렬
    private readonly Dictionary<int, Fn> _fn = [];
    private Dictionary<ulong, List<int>>? _byFingerprint;

    private sealed record Fn(ulong Hash, string[] Tokens, Instruction[] Ins);

    public CodeImage(string path)
    {
        Target = new ScanTarget(path);
        _pe = PeImage.Parse(Target.Bytes, path);
        int pe = BitConverter.ToInt32(Target.Bytes, 0x3C);
        _imageBase = BitConverter.ToUInt64(Target.Bytes, pe + 24 + 24);
        var span = _pe.Span(_pe.Require(".pdata"));
        var list = new List<(int, int)>(span.Length / 12);
        for (int i = 0; i + 12 <= span.Length; i += 12)
        {
            int b = BitConverter.ToInt32(span[i..]), e = BitConverter.ToInt32(span[(i + 4)..]);
            if (b == 0 && e == 0) break;
            list.Add((b, e));
        }
        list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        _functions = list.ToArray();
    }

    private int Offset(int rva)
    {
        foreach (var s in _pe.Sections)
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + (s.VirtualSize > 0 ? Math.Min(s.VirtualSize, s.RawSize) : s.RawSize))
                return s.RawOffset + (rva - s.VirtualAddress); // 파일에 없는 가상 꼬리는 제외
        throw new ToolException($"RVA 0x{rva:x} 이(가) 어느 섹션에도 속하지 않습니다.");
    }

    public int Rva(in Instruction i) => (int)(i.IP - _imageBase);

    public int FileOffset(int rva) => Offset(rva);

    // rva 부터 바이트 수(minBytes) 이상을 덮거나 명령어 수(count)만큼 디코드
    public List<Instruction> DecodeAt(int rva, int minBytes, int count = 0)
    {
        int off = Offset(rva);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Target.Bytes, off, Target.Bytes.Length - off), _imageBase + (ulong)rva);
        var list = new List<Instruction>();
        int covered = 0;
        while (covered < minBytes || list.Count < count)
        {
            decoder.Decode(out var ins);
            if (ins.IsInvalid) break;
            list.Add(ins);
            covered += ins.Length;
        }
        return list;
    }

    public byte[] Bytes(in Instruction i) => Target.Bytes.AsSpan(Offset(Rva(i)), i.Length).ToArray();

    public ConstantOffsets ConstantOffsets(in Instruction i)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Target.Bytes, Offset(Rva(i)), i.Length), i.IP);
        decoder.Decode(out var again);
        return decoder.GetConstantOffsets(again);
    }

    public int? CallTarget(int rva)
    {
        var ins = DecodeAt(rva, 1)[0];
        return ins.FlowControl == FlowControl.Call && ins.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32
            ? (int)(ins.NearBranchTarget - _imageBase) : null;
    }

    private int FunctionIndex(int rva)
    {
        int lo = 0, hi = _functions.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (rva < _functions[mid].Begin) hi = mid - 1;
            else if (rva >= _functions[mid].End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    private Fn Function(int index)
    {
        if (_fn.TryGetValue(index, out var fn)) return fn;
        var (b, e) = _functions[index];
        var ins = DecodeAt(b, e - b);
        while (ins.Count > 0 && Rva(ins[^1]) >= e) ins.RemoveAt(ins.Count - 1);
        var tokens = ins.Select(i => SigRegen.Normalize(i)).ToArray();
        ulong h = 14695981039346656037;
        foreach (string t in tokens) { foreach (char c in t) h = (h ^ c) * 1099511628211; h = (h ^ '|') * 1099511628211; }
        return _fn[index] = new Fn(h, tokens, ins.ToArray());
    }

    // rva 의 n 개 앞 명령어 주소 (같은 함수 안에서만)
    public int? Before(int rva, int n)
    {
        if (Position(rva) is not { } pos || pos.Index < n) return null;
        return Rva(Function(pos.Func).Ins[pos.Index - n]);
    }

    // (함수 번호, 명령어 순번) — 문맥 비교용
    public (int Func, int Index)? Position(int rva)
    {
        int f = FunctionIndex(rva);
        if (f < 0) return null;
        int k = Array.FindIndex(Function(f).Ins, i => Rva(i) == rva);
        return k < 0 ? null : (f, k);
    }

    // 명령어 조건들이 연속으로 맞는 위치 (모든 함수를 훑는다)
    public List<(int Rva, (int Func, int Index) Pos)> FindShape(List<Func<Instruction, bool>> query)
    {
        var result = new List<(int, (int, int))>();
        if (query.Count == 0) return result;
        for (int f = 0; f < _functions.Length; f++)
        {
            var ins = Function(f).Ins;
            for (int i = 0; i + query.Count <= ins.Length; i++)
            {
                int k = 0;
                while (k < query.Count && query[k](ins[i + k])) k++;
                if (k == query.Count) result.Add((Rva(ins[i]), (f, i)));
            }
        }
        return result;
    }

    // 옛 위치와 새 위치의 앞뒤(±12) 명령어 모양이 얼마나 같은지 (±3 어긋남 허용)
    public int ContextScore(CodeImage old, (int Func, int Index) o, (int Func, int Index) n)
    {
        var ot = old.Function(o.Func).Tokens;
        var nt = Function(n.Func).Tokens;
        int best = 0;
        for (int shift = -3; shift <= 3; shift++)
        {
            int score = 0;
            for (int d = -12; d <= 12; d++)
            {
                int a = o.Index + d, b = n.Index + d + shift;
                if (a >= 0 && a < ot.Length && b >= 0 && b < nt.Length && ot[a] == nt[b]) score++;
            }
            best = Math.Max(best, score);
        }
        return best;
    }

    private Dictionary<ulong, List<int>> ByFingerprint()
    {
        if (_byFingerprint != null) return _byFingerprint;
        _byFingerprint = [];
        for (int i = 0; i < _functions.Length; i++)
        {
            ulong h = Function(i).Hash;
            if (!_byFingerprint.TryGetValue(h, out var l)) _byFingerprint[h] = l = [];
            l.Add(i);
        }
        return _byFingerprint;
    }

    // 옛 함수(.pdata 단위)에 대응하는 함수: 지문이 같은 것 (여럿이면 바이트가 가장 비슷한 것)
    private int? MapFunction(CodeImage old, int oldIndex)
    {
        var fn = old.Function(oldIndex);
        if (!ByFingerprint().TryGetValue(fn.Hash, out var same)) return null;
        if (same.Count == 1) return same[0];
        byte[] ob = old.FunctionBytes(oldIndex);
        return same.OrderByDescending(i => Similarity(ob, FunctionBytes(i))).First();
    }

    // 옛 함수 시작 주소 -> 대응 함수 시작. .pdata 에 없는 단순 함수는 ret 까지의 모양이 같은 첫 call 대상을 찾지 않고
    // 같은 모양의 코드 위치를 찾는다.
    public int? MapFunctionStart(CodeImage old, int oldRva)
    {
        int oi = old.FunctionIndex(oldRva);
        if (oi >= 0 && old._functions[oi].Begin == oldRva)
            return MapFunction(old, oi) is { } ni ? _functions[ni].Begin : null;
        // 단순(leaf) 함수: ret 까지의 명령어 모양을 그대로 찾는다 (유일할 때만)
        var leaf = old.LeafInstructions(oldRva);
        if (leaf.Count < 3) return null;
        byte[] first = old.Bytes(leaf[0]);
        byte opcode = first[0] is >= 0x40 and <= 0x4F && first.Length > 1 ? first[1] : first[0];
        var hits = FindLeaf(leaf.Select(i => SigRegen.Normalize(i)).ToArray(), opcode);
        return hits.Count == 1 ? hits[0] : null;
    }

    private List<int> FindLeaf(string[] tokens, byte opcode)
    {
        // 함수 시작은 16바이트 정렬, 첫 명령어의 opcode(REX 제외)가 같은 곳만 디코드해 본다
        var result = new List<int>();
        var text = _pe.Require(".text");
        var span = _pe.Span(text);
        for (int i = (16 - (text.VirtualAddress & 0xF)) & 0xF; i < span.Length - 16; i += 16)
        {
            byte b0 = span[i];
            if (b0 != opcode && !(b0 is >= 0x40 and <= 0x4F && span[i + 1] == opcode)) continue;
            var ins = DecodeAt(text.VirtualAddress + i, 0, tokens.Length);
            if (ins.Count == tokens.Length && ins.Select(x => SigRegen.Normalize(x)).SequenceEqual(tokens))
                result.Add(text.VirtualAddress + i);
        }
        return result;
    }

    // 옛 명령어 주소 -> 대응 함수 안의 같은 순번 명령어 주소 (지문이 같을 때만)
    public int? MapAddress(CodeImage old, int oldRva)
    {
        var pos = old.Position(oldRva);
        if (pos == null || MapFunction(old, pos.Value.Func) is not { } ni) return null;
        var ins = Function(ni).Ins;
        return pos.Value.Index < ins.Length ? Rva(ins[pos.Value.Index]) : null;
    }

    // target 을 부르는(E8 rel32) 곳 중, 옛 call 위치와 뒤쪽 명령어 모양이 가장 비슷한 곳
    public int? BestCaller(CodeImage old, int oldCallRva, int target)
    {
        var text = _pe.Require(".text");
        var span = _pe.Span(text);
        var oldCtx = old.After(oldCallRva);
        int? best = null;
        int bestScore = -1;
        for (int i = 0; i + 5 <= span.Length; i++)
        {
            if (span[i] != 0xE8) continue;
            int rva = text.VirtualAddress + i;
            if (rva + 5 + BitConverter.ToInt32(span[(i + 1)..]) != target) continue;
            int score = oldCtx.Zip(After(rva)).Count(p => p.First == p.Second);
            if (score > bestScore) { bestScore = score; best = rva; }
        }
        return best;
    }

    private string[] After(int rva) => DecodeAt(rva, 0, 9).Skip(1).Select(i => SigRegen.Normalize(i)).ToArray();

    // .pdata 에 없는 함수(스택을 안 쓰는 단순 함수): 시작부터 ret / 꼬리 jmp 까지
    private List<Instruction> LeafInstructions(int rva)
    {
        var list = new List<Instruction>();
        foreach (var ins in DecodeAt(rva, 0, 200))
        {
            list.Add(ins);
            if (ins.FlowControl is FlowControl.Return or FlowControl.Interrupt) break;
            if (ins.FlowControl == FlowControl.UnconditionalBranch && list.Count > 1) break;
        }
        return list;
    }

    // (fndiff) rva 를 감싼 함수의 명령어 모양
    public string[] FunctionTokens(int rva)
    {
        int i = FunctionIndex(rva);
        return i >= 0 ? Function(i).Tokens : LeafInstructions(rva).Select(x => SigRegen.Normalize(x)).ToArray();
    }

    private byte[] FunctionBytes(int index)
    {
        var (b, e) = _functions[index];
        return Target.Bytes.AsSpan(Offset(b), e - b).ToArray();
    }

    private static double Similarity(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;
        int same = 0;
        for (int i = 0; i < n; i++) if (a[i] == b[i]) same++;
        return (double)same / Math.Max(a.Length, b.Length);
    }
}
