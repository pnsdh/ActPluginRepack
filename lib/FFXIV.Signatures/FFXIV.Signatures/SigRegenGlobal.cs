using System.Buffers.Binary;
using Iced.Intel;

namespace FFXIV.Signatures;

// 대체 경로 — 전역 변수 기준. 위치 찾기(옛 위치를 담은 함수의 대응)가 실패해도, 시그니처가 읽던 값이 전역 변수(.data 등)를
// 가리키는 RIP 변위면 그 변수를 쓰는 다른 함수들은 대개 패치에도 그대로 남는다 (OverlayPlugin 7.30 EnmityHud, cactbot 7.4
// kJobData 를 본가가 손으로 고친 것이 이 모양). 그래서:
//   1) 옛 exe 에서 시그니처가 읽던 변수 G 를 RIP 상대로 가리키는 명령어를 모두 찾는다.
//   2) 그 명령어를 담은 함수 중 지문이 옛·새 exe 양쪽에서 하나뿐인 것(MapAddressUnique)마다 새 exe 의 같은 순번 명령어가
//      가리키는 곳 G' 를 얻는다 (명령어 모양과 가리키는 섹션이 같아야 함). 쌍둥이 지문은 쓰지 않는다 — 다른 변수를 쓰는 쌍둥이
//      함수끼리 짝지어지면 엉뚱한 변수를 "근거 여럿"으로 믿게 된다. 근거(서로 다른 새 함수)가 MinAnchors 개 이상이고 모두 같은
//      G' 일 때만 믿는다 — 하나라도 다른 곳을 가리키면 실패.
//   3) 새 exe 에서 G' 를 가리키는 명령어로 한 곳에만 맞는 패턴을 만든다. 자리는 옛 읽는 명령어와 모양·변위 뒤 길이가 같은 것
//      (근거 함수 안의 것 먼저 — 이번 패치를 견딘 코드), 그다음 나머지. 같은 무리 안에서는 늘린 명령어가 적은 것부터, 그 명령어의
//      변위 앞에서 끝나게(offset 0) 앞 명령어로 늘리고, 안 되면 뒤로 늘린다(offset 음수).
//   4) 검증: 새 exe 에서 정확히 한 곳에 맞고 그 자리에서 규칙대로 읽은 값이 G' 여야 하며, 옛 exe 에서 맞는 곳은 모두 G 를 읽어야
//      한다 (다른 변수를 읽는 코드에도 맞는 패턴이면 그 코드를 찾은 것일 수 있다).
// 같은 코드가 아니라 같은 변수를 찾은 것이다 — Result.GlobalAnchors 로 알린다 (근거 함수 수).
public static partial class SigRegen
{
    internal const int MinAnchors = 2;
    private const int MaxReferences = 2048;  // 이보다 흔한 변수는 다루지 않는다 (시간, 그리고 그런 변수는 거의 공용 객체)
    private const int MaxSites = 64;         // 패턴을 만들어 볼 새 참조 위치 수
    private const int MaxExtend = 8;         // 앞/뒤로 늘리는 명령어 수
    private const int MinFixedBytes = 8;     // 만든 패턴의 고정 바이트 수 하한

    // 대체 경로를 쓸 수 있는 필드인지 (아니면 실패 문구에 대체 경로 얘기를 붙이지 않는다)
    private static bool GlobalApplies(ReadField field) => field.Kind == FieldKind.RipDisp && field.Points is not (null or ".text");

    // 근거 투표의 결론: 옛 변수 G 가 새 exe 에서 NewG 로 옮겨졌다 (짝이 분명한 근거 함수 AnchorFns — 새 함수 번호 — 가 모두 그렇게 봄)
    private sealed record GlobalVote(int G, int NewG, HashSet<int> AnchorFns, int Unmapped)
    {
        public string Describe => $"0x{G:x} -> 0x{NewG:x} (근거 함수 {AnchorFns.Count}개{(Unmapped > 0 ? $", 짝이 분명하지 않은 참조 {Unmapped}곳" : "")})";
    }

    private static Result? ViaGlobal(CodeImage oldExe, CodeImage newExe, List<SigPattern.Match> oldMatches, ReadField field, out string why,
                                     GlobalVote? vote = null)
    {
        if (vote == null)
        {
            vote = Vote(oldExe, newExe, oldMatches, field, out why);
            if (vote == null) return null;
        }
        return BuildForGlobal(oldExe, newExe, field, vote, out why);
    }

    // 개수가 같아 "이미 맞음"인 패턴의 확인: 근거 투표의 결론이 있는데 패턴이 새 exe 에서 읽는 값이 그것과 다르면, 근거가 가리키는 곳으로
    // 다시 만든다 (못 만들면 실패). 결론이 없으면(근거 부족·갈림·변수가 너무 흔함 등) null — 지금처럼 이미 맞음으로 둔다.
    // 투표는 두 exe 의 모든 함수 지문을 만들므로 처음 한 번 몇 초 걸린다.
    private static Result? OtherGlobal(CodeImage oldExe, CodeImage newExe, List<SigPattern.Match> oldMatches, List<SigPattern.Match> nowMatches,
                                       ReadField field, int offset, int ripOffset)
    {
        GlobalVote? vote;
        try { vote = Vote(oldExe, newExe, oldMatches, field, out _); }
        catch (SignatureException) { return null; }
        if (vote == null) return null;
        var now = nowMatches.Select(m => SigValue.Resolve(newExe.Image, m, offset, rip: true, ripOffset).ResolvedRva).Distinct().ToList();
        if (now is [{ } only] && only == vote.NewG) return null;
        string reads = string.Join(", ", now.Select(v => v is { } x ? $"0x{x:x}" : "?"));
        string why = $"옛 패턴이 새 exe 에서도 같은 개수로 맞지만 다른 변수({reads})를 읽음 - 그 변수를 쓰는 함수들로 보면 {vote.Describe}";
        Debug(why);
        Result? rebuilt;
        string failed;
        try { rebuilt = BuildForGlobal(oldExe, newExe, field, vote, out failed); }
        catch (SignatureException ex) { (rebuilt, failed) = (null, ex.Message); }
        return rebuilt != null
            ? rebuilt with { Detail = $"{rebuilt.Detail} - {why}", ReadsOtherGlobal = true }
            : new Result(null, $"{why}; 그 위치로 다시 만들지 못함: {failed}") { ReadsOtherGlobal = true };
    }

    // 1) 옛 exe 에서 읽던 변수, 2) 짝이 분명한 함수들이 가리키는 새 위치 — 근거가 MinAnchors 개 이상이고 모두 같을 때만 결론
    private static GlobalVote? Vote(CodeImage oldExe, CodeImage newExe, List<SigPattern.Match> oldMatches, ReadField field, out string why)
    {
        // 1) 옛 exe 에서 읽던 변수
        int? g = null;
        foreach (var m in oldMatches)
        {
            var list = oldExe.DecodeAt(m.Rva, 0, field.Index + 1);
            if (list.Count <= field.Index) { why = $"0x{m.Rva:x}: 옛 exe 에서 명령어를 읽지 못함"; return null; }
            int target = oldExe.RipTarget(list[field.Index]);
            if (g != null && g != target) { why = "일치마다 읽는 전역 변수가 다름"; return null; }
            g = target;
        }
        var oldRefs = oldExe.RipReferences(g!.Value);
        Debug($"global 0x{g:x}: {oldRefs.Count} reference(s) in the old exe");
        if (oldRefs.Count > MaxReferences) { why = $"옛 exe 에서 그 변수(0x{g:x})를 쓰는 곳이 너무 많음 ({oldRefs.Count}곳)"; return null; }

        // 2) 짝이 분명한 함수들이 가리키는 새 위치
        var votes = new Dictionary<int, HashSet<int>>(); // G' -> 근거가 된 새 함수
        int unmapped = 0;
        foreach (int r in oldRefs)
        {
            if (newExe.MapAddressUnique(oldExe, r) is not { } mapped) { unmapped++; continue; }
            var oi = oldExe.DecodeOne(r);
            var ni = newExe.DecodeOne(mapped.Rva);
            if (!ni.IsIPRelativeMemoryOperand || Normalize(oi) != Normalize(ni) ||
                newExe.SectionOf(ni.IPRelativeMemoryAddress) != field.Points) { unmapped++; continue; }
            int gp = newExe.RipTarget(ni);
            if (!votes.TryGetValue(gp, out var fns)) votes[gp] = fns = [];
            fns.Add(mapped.Func);
        }
        Debug($"votes: {string.Join(", ", votes.Select(v => $"0x{v.Key:x} x{v.Value.Count}"))}, unmapped {unmapped}");
        if (votes.Count == 0) { why = $"그 변수(0x{g:x})를 쓰는 함수 중 새 exe 에서 짝이 분명한 것이 없음"; return null; }
        if (votes.Count > 1)
        {
            why = $"그 변수(0x{g:x})를 쓰는 함수들이 새 exe 에서 서로 다른 곳을 가리킴 ({string.Join(", ", votes.Select(v => $"0x{v.Key:x}: {v.Value.Count}개"))})";
            return null;
        }
        var (newG, anchorFns) = (votes.First().Key, votes.First().Value);
        int anchors = anchorFns.Count;
        if (anchors < MinAnchors) { why = $"근거 함수가 {anchors}개뿐 (0x{g:x} -> 0x{newG:x}, {MinAnchors}개 이상 필요)"; return null; }
        why = "";
        return new GlobalVote(g.Value, newG, anchorFns, unmapped);
    }

    // 3) 새 위치를 읽는 패턴, 4) 검증
    private static Result? BuildForGlobal(CodeImage oldExe, CodeImage newExe, ReadField field, GlobalVote vote, out string why)
    {
        var (g, newG, anchorFns, unmapped) = vote;
        int anchors = anchorFns.Count;
        var tiers = newExe.RipReferences(newG)
            .Select(r =>
            {
                var ins = newExe.DecodeOne(r);
                bool same = Normalize(ins, true, true) == field.Shape && RipOffsetOf(newExe, ins) == field.RipOffset;
                return (Rva: r, Tier: same ? (anchorFns.Contains(newExe.FunctionOf(r)) ? 0 : 1) : 2);
            })
            .OrderBy(s => s.Tier).Take(MaxSites)
            .GroupBy(s => s.Tier, s => s.Rva).ToList();
        int tried = 0;
        foreach (var tier in tiers)
        {
            foreach (bool forward in new[] { false, true })
            {
                var dead = new HashSet<int>(); // 더 늘릴 수 없는 자리 (함수 경계, 흐름이 끊김, 엉뚱한 곳에도 맞음)
                for (int n = forward ? 1 : 0; n <= MaxExtend; n++)
                {
                    foreach (int site in tier)
                    {
                        if (dead.Contains(site)) continue;
                        var built = BuildAround(newExe, site, forward ? 0 : n, forward ? n : 0);
                        if (built == null) { dead.Add(site); continue; }
                        var (text, readAt, ripOffset, ins) = built.Value;
                        // 너무 짧은 패턴은 지금 한 곳이어도 다음 패치에 쉽게 여러 곳이 된다
                        if (text.Split(' ').Count(t => t != "?") < MinFixedBytes) continue;
                        if (TryPattern(text) is not { } pattern) continue;
                        tried++;
                        var matches = pattern.Scan(newExe.Target);
                        if (matches.Count != 1) continue;
                        var m = matches[0];
                        int offset = readAt - m.Length;
                        if (SigValue.Resolve(newExe.Image, m, offset, rip: true, ripOffset).ResolvedRva != newG) { dead.Add(site); continue; }
                        // 옛 exe 에서 다른 변수를 읽는 코드에도 맞으면, 새 exe 의 그 자리도 그런 코드일 수 있다 (늘려 보지 않는다 — 다른 자리로)
                        if (pattern.Scan(oldExe.Target).Any(om => SigValue.Resolve(oldExe.Image, om, offset, rip: true, ripOffset).ResolvedRva != g))
                        {
                            Debug($"pattern also reads another variable in the old exe: {text}");
                            dead.Add(site);
                            continue;
                        }
                        why = "";
                        string extra = unmapped > 0 ? $", 짝이 분명하지 않은 참조 {unmapped}곳" : "";
                        return new Result(text,
                            $"전역 변수 기준 대체 경로: 0x{g:x} -> 0x{newG:x} (근거 함수 {anchors}개{extra}), 1곳, 읽는 값: rip disp of {ins}",
                            offset, ripOffset, $"rip disp of {ins}", OldRipOffset: field.RipOffset) { GlobalAnchors = anchors };
                    }
                }
            }
        }
        why = $"새 위치 0x{newG:x} (근거 함수 {anchors}개)를 읽는 곳 {tiers.Sum(t => t.Count())}곳으로 한 곳에만 맞는 패턴을 만들지 못함 (시도 {tried}개)";
        return null;
    }

    private static int RipOffsetOf(CodeImage exe, in Instruction ins)
    {
        var off = exe.ConstantOffsets(ins);
        return ins.Length - (off.DisplacementOffset + off.DisplacementSize);
    }

    // site 의 명령어(새 변수를 RIP 상대로 읽음)를 중심으로 앞 pre 개 / 뒤 post 개 명령어로 패턴을 만든다. post 가 0 이면 패턴은 그
    // 명령어의 변위 앞에서 끝난다. ReadAt: 패턴 시작에서 변위까지 바이트 수, RipOffset: 변위 뒤로 남는 바이트 수.
    // 같은 함수(.pdata) 밖으로 나가거나, 흐름이 끊기는 명령어(jmp, ret, int3) 너머까지 걸치면 null — 그 너머는 다른 흐름이라
    // 배치만 바뀌어도 깨진다.
    private static (string Text, int ReadAt, int RipOffset, Instruction Ins)? BuildAround(CodeImage exe, int site, int pre, int post)
    {
        int? start = pre == 0 ? site : exe.Before(site, pre);
        if (start == null) return null;
        var list = exe.DecodeAt(start.Value, 0, pre + 1 + post);
        if (list.Count < pre + 1 + post || exe.Rva(list[pre]) != site) return null;
        if (list.Take(list.Count - 1).Any(i => i.FlowControl is FlowControl.UnconditionalBranch or FlowControl.Return or FlowControl.Interrupt))
            return null;
        if (post > 0 && exe.FunctionOf(exe.Rva(list[^1])) != exe.FunctionOf(site)) return null;
        var tokens = new List<string>();
        int readAt = 0, ripOffset = 0;
        for (int k = 0; k < list.Count; k++)
        {
            var ins = list[k];
            byte[] bytes = exe.Bytes(ins);
            var off = exe.ConstantOffsets(ins);
            if (k == pre)
            {
                readAt = tokens.Count + off.DisplacementOffset;
                ripOffset = ins.Length - (off.DisplacementOffset + off.DisplacementSize);
            }
            int take = k == pre && post == 0 ? off.DisplacementOffset : bytes.Length;
            for (int b = 0; b < take; b++)
            {
                bool inDisp = off.DisplacementSize > 0 && b >= off.DisplacementOffset && b < off.DisplacementOffset + off.DisplacementSize;
                bool inImm = off.ImmediateSize > 0 && b >= off.ImmediateOffset && b < off.ImmediateOffset + off.ImmediateSize;
                tokens.Add((inDisp && ins.IsIPRelativeMemoryOperand) || (inImm && IsBranch(ins)) ? "?" : bytes[b].ToString("X2"));
            }
        }
        return (string.Join(" ", tokens), readAt, ripOffset, list[pre]);
    }
}

public sealed partial class CodeImage
{
    // RIP 상대 메모리 피연산자가 가리키는 RVA
    internal int RipTarget(in Instruction i) => (int)(i.IPRelativeMemoryAddress - _imageBase);

    // rva 를 담은 .pdata 함수 번호 (없으면 -1)
    internal int FunctionOf(int rva) => FunctionIndex(rva);

    // 옛 명령어 주소 -> 대응 함수의 같은 순번 명령어와 그 함수 번호. 그 함수의 지문이 옛·새 exe 양쪽에서 하나뿐일 때만
    // (MapAddress 는 새 exe 에서 하나뿐이면 옛 exe 의 쌍둥이도 그쪽으로 짝짓는다).
    internal (int Rva, int Func)? MapAddressUnique(CodeImage old, int oldRva)
    {
        if (old.Position(oldRva) is not { } pos) return null;
        ulong h = old.Function(pos.Func).Hash;
        if (old.FingerprintCount(h) != 1 || !ByFingerprint().TryGetValue(h, out var same) || same.Count != 1) return null;
        var rvas = Function(same[0]).Rvas;
        return pos.Index < rvas.Length ? (rvas[pos.Index], same[0]) : null;
    }

    private Dictionary<ulong, int>? _fingerprintCounts;

    // 그 지문인 함수 수. 모든 함수의 지문을 처음 한 번 센다 — 명령어 표(Function)는 남기지 않고 지문만 (옛 exe 전체에 쓰므로
    // ByFingerprint 처럼 모든 함수의 표를 들고 있으면 exe 하나에 100MB 넘게 더 든다)
    private int FingerprintCount(ulong hash)
    {
        if (_byFingerprint != null) return _byFingerprint.TryGetValue(hash, out var list) ? list.Count : 0;
        if (_fingerprintCounts == null)
        {
            var counts = new Dictionary<ulong, int>();
            for (int i = 0; i < _functions.Length; i++)
            {
                ulong h = _fn.TryGetValue(i, out var fn) ? fn.Hash : FingerprintOnly(i);
                counts[h] = counts.GetValueOrDefault(h) + 1;
            }
            _fingerprintCounts = counts;
        }
        return _fingerprintCounts.GetValueOrDefault(hash);
    }

    // Function(index).Hash 와 같은 값을 표 없이
    private ulong FingerprintOnly(int index)
    {
        var sink = new ShapeHash { Func = FnvBasis };
        foreach (var ins in FunctionInstructions(index))
        {
            sink.Full = sink.Bare = FnvBasis;
            SigRegen.WriteShape(ins, ref sink);
            sink.Func = (sink.Func ^ '|') * FnvPrime;
        }
        return sink.Func;
    }

    // target 을 RIP 상대로 가리키는 명령어들 (.pdata 함수 안만). .text 를 바이트 단위로 훑어 "이 4바이트가 변위라면 target 을
    // 가리킴"인 자리(변위 뒤 즉치 0~4바이트)를 찾고, 그 자리를 담은 함수만 디코드해 확인한다.
    internal List<int> RipReferences(int target)
    {
        var text = _pe.Require(".text");
        var span = _pe.Span(text);
        var functions = new SortedSet<int>();
        for (int i = 0; i + 4 <= span.Length; i++)
        {
            long after = target - ((long)text.VirtualAddress + i + 4) - BinaryPrimitives.ReadInt32LittleEndian(span[i..]);
            if (after is < 0 or > 4) continue;
            int f = FunctionIndex(text.VirtualAddress + i);
            if (f >= 0) functions.Add(f);
        }
        var result = new List<int>();
        foreach (int f in functions)
            foreach (var ins in FunctionInstructions(f))
                if (ins.IsIPRelativeMemoryOperand && RipTarget(ins) == target) result.Add(Rva(ins));
        return result;
    }
}
