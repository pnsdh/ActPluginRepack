using System.Text;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

namespace FFXIV.Signatures;

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
//      call 형이 아니면 위치를 확인할 대상이 없으므로 더 엄격하다: 새 패턴의 명령어는 옛 패턴이 덮던 명령어와 모두 같은 모양이어야
//      하고(앞부분으로 줄인 모양으로는 찾지 않는다), 새 패턴이 옛 exe 의 다른 곳(옛 일치와 겹치지 않는 곳)에도 맞으면 그 코드는
//      패치 전부터 있던 다른 곳이므로 늘려 보고, 끝내 그러면 실패로 보고한다 — 진짜 위치의 모양이 바뀌어 사라졌을 때 남은 비슷한
//      다른 곳을 고르지 않게.
//
// 읽는 자리(readOffset)를 주면 — 플러그인이 일치 시작 A, 길이 L 에 대해 A+L+readOffset 의 값을 읽는 시그니처 —
// 옛 exe 에서 그 자리가 어느 명령어의 어느 필드(RIP 변위, 변위, rel32, 즉치)의 시작인지 기억했다가 새 패턴에서
// 같은 필드의 자리로 새 offset 을 낸다. 새 명령어의 모양·필드 크기가 같고, 주소성 필드면 가리키는 섹션도 같아야 한다.
// 맞추지 못하면 실패로 보고한다. (전역 변수 주소 자체는 두 exe 사이에서 같은 대상인지 확인할 방법이 없다.)
public static class SigRegen
{
    // Pattern: 새 패턴 (pelite 표기, 실패·이미 맞음이면 null), Detail: 사람이 읽을 설명,
    // Offset: 새 읽는 자리 (readOffset 을 줬을 때. 이미 맞음이면 그 패턴 그대로의 offset),
    // RipOffset: 읽는 필드가 RIP 변위일 때 새 명령어에서 변위 뒤로 남는 바이트 수 (SigScan 의 ripOffset — 예: cmp byte ptr [rip+x],0 은 1),
    // ReadField: 읽는 필드 설명 (예: "rip disp of lea rcx,[...]"),
    // AlreadyMatches: 옛 패턴이 새 exe 에서도 옛 exe 와 같은 개수로 맞음 (readOffset 을 줬으면 읽는 필드도 같음)
    // OldRipOffset: 옛 명령어의 같은 값 (RipOffset 과 다르면 읽는 쪽의 ripOffset 도 바꿔야 함),
    // CallTargetUnverified: call 형인데 옛 call 대상에 대응하는 함수를 새 exe 에서 찾지 못해 대상이 같은지 확인하지 못함
    public sealed record Result(string? Pattern, string Detail, int? Offset = null, int? RipOffset = null, string? ReadField = null,
                                bool AlreadyMatches = false, int? OldRipOffset = null, bool CallTargetUnverified = false);

    private const int MaxMatches = 16;

    [ThreadStatic] private static Action<string>? _debug;

    // oldPattern 은 pelite 표기. debug 를 주면 위치 찾기 과정을 한 줄씩 넘긴다.
    // 입력 탓의 실패(패턴 형식, 자동 복구가 지원하지 않는 문법, 스캔 시간 초과, 읽지 못하는 자리)는 예외 대신 Pattern 이 null 인
    // Result 로 알린다 (Detail 이 SignatureException 의 문구). null 인자는 ArgumentNullException.
    public static Result Regenerate(CodeImage oldExe, CodeImage newExe, string oldPattern, int? readOffset = null,
                                    Action<string>? debug = null)
    {
        ArgumentNullException.ThrowIfNull(oldExe);
        ArgumentNullException.ThrowIfNull(newExe);
        ArgumentNullException.ThrowIfNull(oldPattern);
        var previous = _debug;
        _debug = debug;
        try { return RegenerateCore(oldExe, newExe, oldPattern, readOffset); }
        catch (SignatureException ex) { return new Result(null, ex.Message); }
        finally { _debug = previous; }
    }

    private static Result RegenerateCore(CodeImage oldExe, CodeImage newExe, string oldPattern, int? readOffset)
    {
        Slot[] slots = Slot.Parse(oldPattern);
        var sig = SigPattern.Parse(oldPattern);
        var oldMatches = sig.Scan(oldExe.Target);
        if (oldMatches.Count == 0)
        {
            // 이미 새 exe 용으로 고친 패턴을 다시 돌린 경우와 구분되게 새 exe 의 개수도 알린다
            int now = sig.Scan(newExe.Target).Count;
            return new Result(null, now > 0 ? $"옛 exe 에서는 맞지 않음 (새 exe 에서는 {now}곳 — 이미 고친 패턴?)" : "옛 exe 에서도 맞지 않음");
        }
        // 위치마다 전체 exe 를 훑으므로 너무 흔한 패턴은 다루지 않는다 (시간·메모리, 그리고 그런 패턴은 위치 대응이 모호)
        if (oldMatches.Count > MaxMatches) return new Result(null, $"옛 exe 에서 {oldMatches.Count}곳에 맞음 - 자동 복구는 {MaxMatches}곳까지만");

        ReadField? field = null;
        if (readOffset is { } ro && (field = FindReadField(oldExe, oldMatches, ro, out string why)) == null)
            return new Result(null, why);

        var nowMatches = sig.Scan(newExe.Target);
        string unverified = "";
        if (nowMatches.Count == oldMatches.Count)
        {
            // call 형은 개수만 같다고 같은 곳이 아니다 — 옛 call 대상에 대응하는 함수가 새 일치의 대상에 있어야 한다
            if (Array.Exists(slots, s => s.Kind == SlotKind.Jump))
            {
                var mappedAll = oldMatches.Select(m => newExe.MapFunctionStart(oldExe, m.Saves[0])).ToList();
                var mapped = mappedAll.OfType<int>().ToHashSet();
                if (mapped.Count > 0 && !mapped.IsSubsetOf(nowMatches.Select(m => m.Saves[0])))
                    return new Result(null, "새 exe 에서도 같은 개수로 맞지만 call 대상이 옛 대상과 대응하지 않음");
                unverified = UnverifiedNote(mappedAll);
            }
            if (field == null) return new Result(null, "새 exe 에서도 이미 맞음" + unverified, AlreadyMatches: true, CallTargetUnverified: unverified != "");
            var same = NewReadOffset(newExe, nowMatches, field, pre: 0, out string sameWhy);
            return same == null
                ? new Result(null, $"새 exe 에서도 맞지만 읽는 자리가 달라짐 - {sameWhy}")
                : new Result(null, $"새 exe 에서도 이미 맞음{unverified}, 읽는 값: {same.Value.Field}", same.Value.Offset, same.Value.RipOffset,
                             same.Value.Field, AlreadyMatches: true, field.RipOffset, unverified != "");
        }

        // 패턴이 끝난 직후의 필드 값(패턴 밖)도 처음엔 위치를 가리는 단서로 쓰고, 못 찾으면 빼고 다시
        var strict = Rebuild(oldExe, newExe, slots, oldMatches, field, looseCut: false, out bool hasCut);
        if (strict.Pattern != null || !hasCut) return strict;
        var loose = Rebuild(oldExe, newExe, slots, oldMatches, field, looseCut: true, out _);
        return loose.Pattern != null ? loose with { Detail = loose.Detail + ", 패턴 밖 값 무시" } : strict;
    }

    // call 대상을 대응시키지 못한 일치가 있으면 그 사실을 Detail 에 붙일 문구 (모두 대응했으면 "")
    private static string UnverifiedNote(IReadOnlyCollection<int?> mapped) =>
        !mapped.Any(t => t == null) ? ""
        : mapped.All(t => t == null) ? ", call 대상은 확인하지 못함 (대응하는 함수를 못 찾음)"
        : ", call 대상 일부는 확인하지 못함 (대응하는 함수를 못 찾음)";

    // 새 패턴이 옛 exe 에서 옛 일치와 겹치지 않는 곳에 맞는 수. 0 이 아니면 그 코드는 패치 전부터 있던 다른 곳이라,
    // 새 exe 에서 고른 위치가 옛 위치에 대응한다고 볼 수 없다 (call 형이 아닌 패턴은 위치 말고는 확인할 것이 없다).
    private static int StraysInOld(CodeImage oldExe, List<SigPattern.Match> oldMatches, SigPattern pattern) =>
        pattern.Scan(oldExe.Target).Count(h => !oldMatches.Any(m => h.Rva < m.End && m.Rva < h.End));

    private static SigPattern? TryPattern(string text) => SigPattern.TryParse(text, out var p) ? p : null;

    private static Result Rebuild(CodeImage oldExe, CodeImage newExe, Slot[] slots, List<SigPattern.Match> oldMatches,
                                  ReadField? field, bool looseCut, out bool hasCut)
    {
        int jumpSlot = Array.FindIndex(slots, s => s.Kind == SlotKind.Jump);
        bool isJump = jumpSlot >= 0;
        var layout = Layout(oldExe, oldMatches[0].Rva, slots, looseCut); // 옛 패턴이 덮는 명령어 구성 (첫 일치 기준)
        hasCut = layout.Any(p => p.CutAtDisp || p.CutAtImm);
        if (layout.Count == 0) return new Result(null, $"0x{oldMatches[0].Rva:x}: 옛 exe 에서 명령어를 읽지 못함");

        // ---- 1) 위치 찾기 ----
        var sites = new List<int>();
        var targets = new List<int?>();
        var hows = new List<string>();
        var mappedTargets = isJump ? oldMatches.Select(m => newExe.MapFunctionStart(oldExe, m.Saves[0])).ToList() : null;
        // 전체 모양이 새 exe 에서 옛 일치 수와 똑같은 개수만큼만 나오면, 위치마다 짝을 짓지 않고 그대로 쓴다
        // (어차피 그 위치들 전체로 패턴을 만들고 검증한다 — 7.57 RECV 처럼 주변 코드가 많이 바뀐 경우)
        var whole = newExe.FindShape(QueryParts(layout).Select(p => p.Test).ToList());
        Debug($"query: {string.Join(" ; ", QueryParts(layout).Select(p => p.Shape))}");
        Debug($"whole-shape candidates: {whole.Count} ({string.Join(",", whole.Take(10).Select(c => c.Rva.ToString("x")))})");
        var group = whole.Count >= oldMatches.Count ? PickGroup(oldExe, newExe, oldMatches, slots, layout, whole, checkOld: !isJump) : null;
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
            hows.Add("전체 모양 묶음");
        }
        else
        {
            var used = new HashSet<int>();
            for (int i = 0; i < oldMatches.Count; i++)
            {
                var m = oldMatches[i];
                int? newTarget = mappedTargets?[i];
                int? site = FindSite(oldExe, newExe, m.Rva, layout, isJump, newTarget, used, out string how);
                if (site == null) return new Result(null, $"0x{m.Rva:x} 에 대응하는 위치를 새 exe 에서 못 찾음");
                used.Add(site.Value);
                sites.Add(site.Value);
                targets.Add(newTarget);
                hows.Add(how);
            }
        }
        Debug($"sites: {string.Join(",", sites.Select(s => s.ToString("x")))} ({string.Join(" / ", hows.Distinct())})");

        // ---- 2) 새 패턴, 3) 검증 ----
        // 옛 패턴이 명령어 중간에서 끝났으면(플러그인 메모리 시그니처처럼 패턴 바로 뒤 값을 읽음) 끝 위치를 지켜야 하므로
        // 넓을 때 뒤가 아니라 앞 명령어로 늘린다. 시작 위치가 중요한 후크 시그니처는 뒤로 늘린다.
        bool keepEnd = !isJump && layout[^1].Partial;
        string? last = null;
        int strays = 0;
        for (int extra = 0; extra <= 8; extra++)
        {
            int pre = keepEnd ? extra : 0;
            var starts = sites.Select(s => pre == 0 ? s : newExe.Before(s, pre)).ToList();
            if (starts.Any(s => s == null)) break;
            var perSite = sites.Select(s => Build(newExe, s, layout, extra, keepEnd)).ToList();
            if (perSite.Any(p => p == null)) return new Result(null, "새 위치에서 옛 패턴과 같은 모양의 명령어를 읽지 못함");
            var merged = Merge(perSite!);
            if (merged == null) return new Result(null, "위치마다 명령어 구성이 다름");
            last = string.Join(" ", merged);
            if (TryPattern(last) is not { } built) return new Result(null, $"새 패턴을 만들지 못함 (고정 바이트가 없음 등): {last}");
            var matches = built.Scan(newExe.Target);
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
            else if ((strays = StraysInOld(oldExe, oldMatches, built)) > 0)
            {
                // call 형이 아니면 위치가 맞는지 확인할 다른 방법이 없다 — 옛 exe 의 다른 곳에도 맞으면 늘려서 다시 본다
                Debug($"new pattern also matches {strays} other place(s) in the old exe: {last}");
                continue;
            }
            string detail = $"{matches.Count}곳" + (extra > 0 ? $", {(keepEnd ? "앞" : "뒤")} 명령어 {extra}개 늘림" : "")
                          + $", 위치: {string.Join(" / ", hows.Distinct())}";
            // 하나라도 대응 함수를 못 찾았으면 그 일치의 대상은 확인하지 않은 것이다
            bool targetUnverified = isJump && mappedTargets!.Any(t => t == null);
            if (isJump) detail += UnverifiedNote(mappedTargets!);
            if (field == null) return new Result(last, detail, CallTargetUnverified: targetUnverified);
            var read = NewReadOffset(newExe, matches, field, pre, out string readWhy);
            if (read == null) return new Result(null, $"새 패턴은 만들었지만 읽는 자리를 맞추지 못함 - {readWhy}: {last}");
            return new Result(last, $"{detail}, 읽는 값: {read.Value.Field}", read.Value.Offset, read.Value.RipOffset, read.Value.Field,
                              OldRipOffset: field.RipOffset, CallTargetUnverified: targetUnverified);
        }
        return new Result(null, strays > 0
            ? $"새 패턴이 옛 exe 의 다른 곳({strays}곳)에도 맞음 — 패치 전부터 있던 다른 코드일 수 있어 대응 위치로 볼 수 없음: {last}"
            : $"새 패턴이 너무 넓음: {last}");
    }

    private static void Debug(string s) => _debug?.Invoke(s);

    // ---- 읽는 자리 ----

    private enum FieldKind { RipDisp, Disp, Rel32, Imm, Imm2 }

    // 일치 시작부터 Index 번째 명령어의 Kind 필드 (크기 Size). Shape: 값을 뺀 명령어 모양, Points: 주소성 필드가 가리키는 섹션
    // RipOffset: RIP 변위면 옛 명령어에서 변위 뒤로 남는 바이트 수
    private sealed record ReadField(int Index, FieldKind Kind, int Size, string Shape, string? Points, int? RipOffset);

    private static string Label(FieldKind k) => k switch
    {
        FieldKind.RipDisp => "rip disp", FieldKind.Disp => "disp", FieldKind.Rel32 => "rel32", _ => "imm",
    };

    // 명령어의 필드 (offset, size) — 없으면 size 0
    private static (int Offset, int Size) FieldOf(in ConstantOffsets off, FieldKind kind) => kind switch
    {
        FieldKind.RipDisp or FieldKind.Disp => (off.DisplacementOffset, off.DisplacementSize),
        FieldKind.Imm2 => (off.ImmediateOffset2, off.ImmediateSize2),
        _ => (off.ImmediateOffset, off.ImmediateSize),
    };

    private static string? Points(CodeImage exe, in Instruction ins, FieldKind kind) => kind switch
    {
        FieldKind.RipDisp => exe.SectionOf(ins.IPRelativeMemoryAddress),
        FieldKind.Rel32 => exe.SectionOf(ins.NearBranchTarget),
        _ => null,
    };

    private static ReadField? FindReadField(CodeImage exe, List<SigPattern.Match> matches, int readOffset, out string why)
    {
        ReadField? found = null;
        foreach (var m in matches)
        {
            int r = m.Length + readOffset; // 일치 시작에서 읽는 자리까지
            if (r < 0) { why = $"읽는 자리(offset {readOffset})가 패턴 시작보다 앞"; return null; }
            var list = exe.DecodeAt(m.Rva, r + 1);
            int pos = 0, k = 0;
            while (k < list.Count && pos + list[k].Length <= r) pos += list[k++].Length;
            if (k >= list.Count) { why = $"0x{m.Rva:x}: 읽는 자리까지 명령어를 읽지 못함"; return null; }
            var ins = list[k];
            var off = exe.ConstantOffsets(ins);
            int b = r - pos;
            FieldKind? kind =
                off.DisplacementSize > 0 && b == off.DisplacementOffset ? (ins.IsIPRelativeMemoryOperand ? FieldKind.RipDisp : FieldKind.Disp)
                : off.ImmediateSize > 0 && b == off.ImmediateOffset ? (IsBranch(ins) ? FieldKind.Rel32 : FieldKind.Imm)
                : off.ImmediateSize2 > 0 && b == off.ImmediateOffset2 ? FieldKind.Imm2
                : null;
            if (kind == null) { why = $"0x{m.Rva:x}: 읽는 자리가 '{ins}' 의 변위/즉치 시작이 아님 (명령어 안 {b}번째 바이트)"; return null; }
            var (fieldAt, fieldSize) = FieldOf(off, kind.Value);
            var field = new ReadField(k, kind.Value, fieldSize, Normalize(ins, true, true), Points(exe, ins, kind.Value),
                                      kind is FieldKind.RipDisp or FieldKind.Rel32 ? ins.Length - (fieldAt + fieldSize) : null);
            if (found != null && found != field) { why = "일치마다 읽는 자리의 명령어/필드가 다름"; return null; }
            found = field;
        }
        Debug($"read field: #{found!.Index} {Label(found.Kind)} ({found.Size}B) {found.Shape} -> {found.Points ?? "-"}");
        why = "";
        return found;
    }

    // 새 패턴의 일치들에서 같은 필드의 자리 -> 새 offset (일치마다 같아야 함). pre: 새 패턴이 앞에 덧붙인 명령어 수
    private static (int Offset, string Field, int? RipOffset)? NewReadOffset(CodeImage exe, List<SigPattern.Match> matches, ReadField field, int pre,
                                                            out string why)
    {
        (int Offset, string Field, int? RipOffset)? result = null;
        foreach (var m in matches)
        {
            int index = pre + field.Index;
            var list = exe.DecodeAt(m.Rva, 0, index + 1);
            if (list.Count <= index) { why = $"0x{m.Rva:x}: 명령어를 읽지 못함"; return null; }
            var ins = list[index];
            if (Normalize(ins, true, true) != field.Shape) { why = $"0x{m.Rva:x}: 그 자리의 명령어 모양이 다름 ('{ins}')"; return null; }
            var off = exe.ConstantOffsets(ins);
            var (fo, size) = FieldOf(off, field.Kind);
            bool kindOk = field.Kind switch
            {
                FieldKind.RipDisp => ins.IsIPRelativeMemoryOperand,
                FieldKind.Disp => !ins.IsIPRelativeMemoryOperand,
                FieldKind.Rel32 => IsBranch(ins),
                _ => true,
            };
            if (!kindOk || size != field.Size) { why = $"0x{m.Rva:x}: '{ins}' 에 같은 필드({Label(field.Kind)} {field.Size}바이트)가 없음"; return null; }
            string? points = Points(exe, ins, field.Kind);
            if (points != field.Points) { why = $"0x{m.Rva:x}: 읽는 값이 가리키는 섹션이 다름 ({field.Points ?? "없음"} -> {points ?? "없음"})"; return null; }
            int r = exe.Rva(ins) - m.Rva + fo;
            int? ripOffset = field.Kind is FieldKind.RipDisp or FieldKind.Rel32 ? ins.Length - (fo + size) : null;
            var one = (Offset: r - m.Length, Field: $"{Label(field.Kind)} of {ins}", RipOffset: ripOffset);
            if (result != null && (result.Value.Offset != one.Offset || result.Value.RipOffset != one.RipOffset))
            { why = "일치마다 새 offset 이 다름"; return null; }
            result ??= one;
        }
        why = "";
        return result;
    }

    // ---- 옛 패턴이 덮는 명령어 구성 ----

    // 명령어 하나에 대해: 덮은 바이트 수, 옛 패턴이 와일드카드였던 필드, call 점프 여부, 1바이트 대안,
    // Shape: 와일드카드 필드를 뺀 옛 명령어 모양 (모양 비교마다 다시 만들지 않게 미리), Test: 같은 조건의 exe 전체 검색용
    private sealed record Part(Instruction Ins, int Covered, bool WildImm, bool WildDisp, bool Jump, Dictionary<int, Slot> Alts,
                               bool CutAtDisp, bool CutAtImm, string Shape, CodeImage.ShapeTest Test)
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
            bool wildImm = Wild(off.ImmediateOffset, off.ImmediateSize) || cutImm;
            bool wildDisp = Wild(off.DisplacementOffset, off.DisplacementSize) || cutDisp;
            string shape = Normalize(ins, wildImm, wildDisp);
            parts.Add(new Part(ins, covered, wildImm, wildDisp,
                Jump: s.Length >= 2 && s[0].Kind == SlotKind.Fixed && s[0].Value == 0xE8 && s[1].Kind == SlotKind.Jump,
                Alts: alts,
                CutAtDisp: covered < ins.Length && off.DisplacementSize > 0 && covered == off.DisplacementOffset,
                CutAtImm: covered < ins.Length && off.ImmediateSize > 0 && covered == off.ImmediateOffset,
                Shape: shape,
                Test: new CodeImage.ShapeTest(shape, wildImm, wildDisp, Normalize(ins, true, true))));
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
        // call 형: 앞부분이 최소 2개 명령어(또는 전부)가 남을 때까지 줄여 가며 모양 검색 (call 대상으로 거르고 검증하므로
        // 뒤쪽 명령어가 바뀌어도 된다). call 형이 아니면 새 패턴의 모든 명령어가 옛 모양과 같아야 하므로(Build) 줄이지 않는다
        // — 줄인 모양에만 맞는 곳은 다른 코드일 가능성이 크다.
        for (int n = query.Count; n >= (isJump ? Math.Min(2, query.Count) : query.Count); n--)
        {
            var q = query.Take(n).ToList();
            var cands = newExe.FindShape(q.Select(p => p.Test).ToList())
                .Where(c => !used.Contains(c.Rva))
                .Where(c => !isJump || newTarget == null || newExe.CallTarget(c.Rva) == newTarget)
                .Where(c => isJump || Build(newExe, c.Rva, layout, 0, false) != null)
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
    // checkOld (call 형이 아닐 때): 그 패턴이 옛 exe 의 다른 곳(옛 일치와 겹치지 않는 곳)에도 맞는 묶음은 패치 전부터 있던
    // 다른 코드이므로 뺀다 — 진짜 위치의 모양이 바뀌어 사라졌을 때 남은 다른 곳 하나를 고르지 않게.
    private static List<int>? PickGroup(CodeImage oldExe, CodeImage newExe, List<SigPattern.Match> oldMatches, Slot[] slots,
                                        List<Part> layout, List<(int Rva, (int Func, int Index) Pos)> cands, bool checkOld)
    {
        var groups = cands
            .Select(c => (c.Rva, c.Pos, Tokens: Build(newExe, c.Rva, layout, 0, false)))
            .Where(c => c.Tokens != null)
            .GroupBy(c => string.Join(" ", c.Tokens!))
            .Where(g => g.Count() == oldMatches.Count)
            .Select(g => (Group: g, Pattern: TryPattern(g.Key)))
            .Where(x => x.Pattern != null && x.Pattern.Scan(newExe.Target).Select(m => m.Rva).Order()
                        .SequenceEqual(x.Group.Select(c => c.Rva).Order()))
            .Where(x => !checkOld || StraysInOld(oldExe, oldMatches, x.Pattern!) == 0)
            .Select(x => x.Group)
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
        p.Ins.Mnemonic == ins.Mnemonic && p.Ins.OpCount == ins.OpCount && p.Shape == Normalize(ins, p.WildImm, p.WildDisp);

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
            // call 형이 아니면 옛 패턴이 덮던 명령어는 모두 같은 모양이어야 한다 (모양이 다른 명령어로 만든 패턴은 다른 코드를 가리킬 수 있다).
            // call 형은 대상 함수로 검증하므로 뒤쪽이 바뀐 명령어를 그대로 쓰거나(다 덮은 것) 뺀다(부분).
            if (p != null && !isJump && !same) return null;

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
    internal static string Normalize(in Instruction i, bool dropImm = false, bool dropDisp = false)
    {
        var sink = new ShapeText(new StringBuilder(), dropImm, dropDisp);
        WriteShape(i, ref sink);
        return sink.Sb.ToString();
    }

    // 모양의 조각을 받는 쪽: 문자열(Normalize) 또는 해시(CodeImage 의 명령어 표 — 문자열을 남기지 않게).
    // Disp: 구조체 오프셋('+' 와 그 값, dropDisp 면 통째로 뺌), Imm: '#' 뒤의 값(dropImm 면 뺌)
    internal interface IShapeSink
    {
        void Text(string s);
        void Char(char c);
        void Number(int v);
        void Disp(ulong v);
        void Imm(ulong v);
    }

    private struct ShapeText(StringBuilder sb, bool dropImm, bool dropDisp) : IShapeSink
    {
        public readonly StringBuilder Sb = sb;
        public readonly void Text(string s) => Sb.Append(s);
        public readonly void Char(char c) => Sb.Append(c);
        public readonly void Number(int v) => Sb.Append(v);
        public readonly void Disp(ulong v) { if (!dropDisp) Sb.Append('+').Append(v.ToString("x")); }
        public readonly void Imm(ulong v) { if (!dropImm) Sb.Append(v.ToString("x")); }
    }

    // Normalize 의 본체. 조각 순서·글자가 곧 모양 문자열이다 (해시 쪽도 같은 조각을 같은 글자로 받는다).
    internal static void WriteShape<T>(in Instruction i, ref T s) where T : struct, IShapeSink
    {
        s.Text(i.Mnemonic.ToString());
        for (int k = 0; k < i.OpCount; k++)
        {
            s.Char(k == 0 ? ' ' : ',');
            switch (i.GetOpKind(k))
            {
                case OpKind.Register:
                    var r = i.GetOpRegister(k);
                    if (r.IsGPR()) { s.Char('r'); s.Number(r.GetSize()); }
                    else s.Text(r.IsXMM() ? "x" : r.IsYMM() ? "y" : r.ToString());
                    break;
                case OpKind.Memory:
                    s.Char('m'); s.Text(i.MemorySize.ToString());
                    // 구조체 오프셋은 남기고(대개 안 바뀜) 스택/RIP 변위는 뺀다
                    if (!i.IsIPRelativeMemoryOperand && i.MemoryBase != Register.None
                        && i.MemoryBase is not (Register.RSP or Register.RBP or Register.ESP))
                        s.Disp(i.MemoryDisplacement64);
                    break;
                case OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64:
                    s.Char('b');
                    break;
                case OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64
                    or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64:
                    s.Char('#');
                    s.Imm(i.GetImmediate(k));
                    break;
                default:
                    s.Text(i.GetOpKind(k).ToString());
                    break;
            }
        }
    }

    // ---- 옛 패턴의 바이트 칸 ----

    private enum SlotKind { Fixed, Wild, Alt, Jump }

    private sealed record Slot(SlotKind Kind, byte Value = 0, byte[]? Values = null, string Text = "")
    {
        // 바이트 칸 단위로 푼다. $ { ' } 는 4칸(Jump). 대안은 "(48 | 4C)" 처럼 1바이트 고정값끼리만.
        // 미지원: $ { } 밖의 북마크('), 건너뛰기([n], [n-m]), 여러 바이트·와일드카드가 든 대안. 문법 오류도 SignatureException.
        public static Slot[] Parse(string s)
        {
            var tokens = SigPattern.Tokenize(s);
            var result = new List<Slot>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                switch (t.Kind)
                {
                    case SigPattern.TokenKind.Byte: result.Add(new Slot(SlotKind.Fixed, t.Value)); break;
                    case SigPattern.TokenKind.Wild: result.Add(new Slot(SlotKind.Wild)); break;
                    case SigPattern.TokenKind.Jump: for (int k = 0; k < 4; k++) result.Add(new Slot(SlotKind.Jump)); break;
                    // $ { } 밖의 북마크는 deucalion 이 후킹 위치로 쓰는데, 다시 만든 패턴에서 위치를 옮길 수 없다
                    case SigPattern.TokenKind.Save: throw new SignatureException("자동 복구는 $ { } 밖의 북마크(')를 지원하지 않습니다.");
                    case SigPattern.TokenKind.Skip: throw new SignatureException("자동 복구는 건너뛰기([n], [n-m])를 지원하지 않습니다.");
                    case SigPattern.TokenKind.Open:
                    {
                        // Byte (Bar Byte)* Close 만
                        var values = new List<byte>();
                        int k = i + 1;
                        while (true)
                        {
                            if (tokens[k].Kind != SigPattern.TokenKind.Byte) throw new SignatureException("자동 복구는 1바이트 고정값 대안만 지원합니다.");
                            values.Add(tokens[k++].Value);
                            if (tokens[k].Kind == SigPattern.TokenKind.Close) break;
                            if (tokens[k++].Kind != SigPattern.TokenKind.Bar) throw new SignatureException("자동 복구는 1바이트 고정값 대안만 지원합니다.");
                        }
                        result.Add(new Slot(SlotKind.Alt, Values: [.. values], Text: s[t.Start..tokens[k].End]));
                        i = k;
                        break;
                    }
                    default: throw new SignatureException($"자동 복구가 지원하지 않는 패턴 토큰입니다: '{s[t.Start]}'");
                }
            }
            return result.ToArray();
        }
    }
}

// 디스어셈블과 함수/위치 대응을 위한 exe 표현 (.text + .pdata). 크다 (exe 파일 크기의 몇 배) — 같은 exe 는 한 번만 만들어 돌려 쓸 것.
// 안에 캐시가 있어 여러 스레드에서 동시에 쓰면 안 된다 (ScanTarget / SigPattern 은 된다).
public sealed class CodeImage
{
    public ScanTarget Target { get; }
    public PeImage Image => _pe;
    private readonly PeImage _pe;
    private readonly ulong _imageBase;
    private readonly (int Begin, int End)[] _functions; // RVA, 시작 주소 정렬
    private readonly Dictionary<int, Fn> _fn = [];
    private Dictionary<ulong, List<int>>? _byFingerprint;

    // 함수 하나의 명령어 표. 명령어 자체나 모양 문자열은 남기지 않는다 (exe 하나에 명령어가 수백만 개 — 그대로 두면 수백 MB):
    // Rvas: 명령어 시작, Full: 모양(Normalize) 해시, Bare: 값까지 뺀 모양(Normalize(dropImm, dropDisp)) 해시 (명령어 해시는
    // 거르기용이라 32비트 — 같으면 다시 디코드해 문자열로 확인하므로 충돌해도 결과는 같다),
    // Hash: 함수 지문 (모양 문자열들을 이어 붙인 64비트 FNV — 예전과 같은 값).
    internal sealed record Fn(ulong Hash, int[] Rvas, uint[] Full, uint[] Bare);

    private const ulong FnvBasis = 14695981039346656037, FnvPrime = 1099511628211;

    internal static uint Fold(ulong h) => (uint)(h ^ (h >> 32));

    internal static ulong HashShape(string s)
    {
        ulong h = FnvBasis;
        foreach (char c in s) h = (h ^ c) * FnvPrime;
        return h;
    }

    // WriteShape 의 조각을 문자열 없이 해시로: Full 은 Normalize(i), Bare 는 Normalize(i, true, true), Func 는 함수 지문에 이어서
    private struct ShapeHash : SigRegen.IShapeSink
    {
        public ulong Full, Bare, Func;

        private void Feed(char c, bool bare)
        {
            Full = (Full ^ c) * FnvPrime;
            Func = (Func ^ c) * FnvPrime;
            if (bare) Bare = (Bare ^ c) * FnvPrime;
        }

        private void Feed(ReadOnlySpan<char> s, bool bare) { foreach (char c in s) Feed(c, bare); }

        public void Text(string s) => Feed(s, true);
        public void Char(char c) => Feed(c, true);

        public void Number(int v)
        {
            Span<char> buf = stackalloc char[16];
            v.TryFormat(buf, out int n); // StringBuilder.Append(int) 와 같은 글자
            Feed(buf[..n], true);
        }

        public void Disp(ulong v)
        {
            Feed('+', false);
            Hex(v, false);
        }

        public void Imm(ulong v) => Hex(v, false);

        private void Hex(ulong v, bool bare)
        {
            Span<char> buf = stackalloc char[16];
            v.TryFormat(buf, out int n, "x"); // ToString("x") 와 같은 글자
            Feed(buf[..n], bare);
        }
    }

    // FindShape 의 명령어 조건 하나: Normalize(ins, dropImm, dropDisp) == shape. bareShape = Normalize(같은 명령어, true, true).
    // 해시로 먼저 거르고(값을 뺀 모양이 다르면 어떤 dropImm/dropDisp 로도 다르다) 해시가 같을 때만 디코드해 문자열로 비교한다.
    internal sealed class ShapeTest(string shape, bool dropImm, bool dropDisp, string bareShape)
    {
        private readonly bool _full = !dropImm && !dropDisp;
        private readonly uint _hash = Fold(HashShape(!dropImm && !dropDisp ? shape : bareShape));

        internal bool Matches(CodeImage exe, Fn fn, int j) =>
            (_full ? fn.Full[j] : fn.Bare[j]) == _hash && SigRegen.Normalize(exe.DecodeOne(fn.Rvas[j]), dropImm, dropDisp) == shape;
    }

    public CodeImage(string path) : this(new ScanTarget(path)) { }

    // 이미 읽은 exe 로 (같은 exe 를 스캔에도 쓸 때 파일과 .text 문자열을 두 번 들고 있지 않게). Text 범위의 ScanTarget 만.
    public CodeImage(ScanTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Scope != ScanScope.Text)
            throw new ArgumentException("CodeImage 에는 Text 범위의 ScanTarget 만 쓸 수 있습니다.", nameof(target));
        Target = target;
        _pe = Target.Image;
        _imageBase = _pe.ImageBase;
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

    // 파일에 없는 가상 꼬리는 제외
    private int Offset(int rva) =>
        _pe.FileOffset(rva) ?? throw new SignatureException($"RVA 0x{rva:x} 이(가) 어느 섹션에도 속하지 않습니다.");

    internal int Rva(in Instruction i) => (int)(i.IP - _imageBase);

    internal int FileOffset(int rva) => Offset(rva);

    // 가상 주소(디스어셈블 결과의 절대 주소)가 속한 섹션 이름
    internal string? SectionOf(ulong va) => va >= _imageBase ? _pe.SectionAt((long)(va - _imageBase))?.Name : null;

    // rva 부터 바이트 수(minBytes) 이상을 덮거나 명령어 수(count)만큼 디코드
    internal List<Instruction> DecodeAt(int rva, int minBytes, int count = 0)
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

    // rva 의 명령어 하나 (명령어 표에서 해시가 같을 때 확인용 — 디코더를 돌려 쓴다)
    private ByteArrayCodeReader? _oneReader;
    private Decoder? _oneDecoder;

    internal Instruction DecodeOne(int rva)
    {
        int off = Offset(rva);
        _oneReader ??= new ByteArrayCodeReader(Target.Bytes);
        _oneDecoder ??= Decoder.Create(64, _oneReader);
        _oneReader.Position = off;
        _oneDecoder.IP = _imageBase + (ulong)rva;
        _oneDecoder.Decode(out var ins);
        return ins;
    }

    internal byte[] Bytes(in Instruction i) => Slice(Offset(Rva(i)), i.Length);

    // 파일 끝을 넘지 않게 (잘린 exe 에서 알아보기 어려운 예외 대신 짧게)
    private byte[] Slice(int off, int length) => Target.Bytes.AsSpan(off, Math.Min(length, Target.Bytes.Length - off)).ToArray();

    internal ConstantOffsets ConstantOffsets(in Instruction i)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Target.Bytes, Offset(Rva(i)), i.Length), i.IP);
        decoder.Decode(out var again);
        return decoder.GetConstantOffsets(again);
    }

    internal int? CallTarget(int rva)
    {
        if (_pe.FileOffset(rva) == null) return null;
        var list = DecodeAt(rva, 1);
        if (list.Count == 0) return null; // 명령어로 읽히지 않는 자리
        var ins = list[0];
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

    // .pdata 함수 하나의 명령어 (끝 주소를 넘는 것은 뺀다)
    private List<Instruction> FunctionInstructions(int index)
    {
        var (b, e) = _functions[index];
        var ins = DecodeAt(b, e - b);
        while (ins.Count > 0 && Rva(ins[^1]) >= e) ins.RemoveAt(ins.Count - 1);
        return ins;
    }

    private Fn Function(int index)
    {
        if (_fn.TryGetValue(index, out var fn)) return fn;
        var ins = FunctionInstructions(index);
        var rvas = new int[ins.Count];
        var full = new uint[ins.Count];
        var bare = new uint[ins.Count];
        var sink = new ShapeHash { Func = FnvBasis };
        for (int k = 0; k < ins.Count; k++)
        {
            sink.Full = sink.Bare = FnvBasis;
            SigRegen.WriteShape(ins[k], ref sink);
            sink.Func = (sink.Func ^ '|') * FnvPrime;
            rvas[k] = Rva(ins[k]);
            full[k] = Fold(sink.Full);
            bare[k] = Fold(sink.Bare);
        }
        return _fn[index] = new Fn(sink.Func, rvas, full, bare);
    }

    // rva 의 n 개 앞 명령어 주소 (같은 함수 안에서만)
    internal int? Before(int rva, int n)
    {
        if (Position(rva) is not { } pos || pos.Index < n) return null;
        return Function(pos.Func).Rvas[pos.Index - n];
    }

    // (함수 번호, 명령어 순번) — 문맥 비교용
    internal (int Func, int Index)? Position(int rva)
    {
        int f = FunctionIndex(rva);
        if (f < 0) return null;
        int k = Array.IndexOf(Function(f).Rvas, rva);
        return k < 0 ? null : (f, k);
    }

    // 명령어 조건들이 연속으로 맞는 위치 (모든 함수를 훑는다)
    internal List<(int Rva, (int Func, int Index) Pos)> FindShape(IReadOnlyList<ShapeTest> query)
    {
        var result = new List<(int, (int, int))>();
        if (query.Count == 0) return result;
        for (int f = 0; f < _functions.Length; f++)
        {
            var fn = Function(f);
            for (int i = 0; i + query.Count <= fn.Rvas.Length; i++)
            {
                int k = 0;
                while (k < query.Count && query[k].Matches(this, fn, i + k)) k++;
                if (k == query.Count) result.Add((fn.Rvas[i], (f, i)));
            }
        }
        return result;
    }

    // 옛 위치와 새 위치의 앞뒤(±12) 명령어 모양이 얼마나 같은지 (±3 어긋남 허용).
    // 모양은 해시로 비교하고, 해시가 같으면 디코드해 문자열로 확인한다 (해시 충돌로 점수가 달라지지 않게).
    internal int ContextScore(CodeImage old, (int Func, int Index) o, (int Func, int Index) n)
    {
        var of = old.Function(o.Func);
        var nf = Function(n.Func);
        // 확인한 모양 문자열 (창 안의 명령어마다 한 번만 디코드)
        var oText = new string?[25];
        var nText = new string?[31];
        int best = 0;
        for (int shift = -3; shift <= 3; shift++)
        {
            int score = 0;
            for (int d = -12; d <= 12; d++)
            {
                int a = o.Index + d, b = n.Index + d + shift;
                if (a < 0 || a >= of.Rvas.Length || b < 0 || b >= nf.Rvas.Length || of.Full[a] != nf.Full[b]) continue;
                string ot = oText[d + 12] ??= SigRegen.Normalize(old.DecodeOne(of.Rvas[a]));
                string nt = nText[d + shift + 15] ??= SigRegen.Normalize(DecodeOne(nf.Rvas[b]));
                if (ot == nt) score++;
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

    // 옛 함수(.pdata 단위)에 대응하는 함수: 지문이 같은 것 (여럿이면 바이트가 가장 비슷한 것 — 1등이 하나일 때만.
    // 동점이면 어느 쪽인지 모르므로 대응하지 않는다: 엉뚱한 쌍둥이를 "확인됨"으로 쓰지 않게)
    private int? MapFunction(CodeImage old, int oldIndex)
    {
        var fn = old.Function(oldIndex);
        if (!ByFingerprint().TryGetValue(fn.Hash, out var same)) return null;
        if (same.Count == 1) return same[0];
        byte[] ob = old.FunctionBytes(oldIndex);
        var ranked = same.Select(i => (Index: i, Score: Similarity(ob, FunctionBytes(i)))).OrderByDescending(x => x.Score).ToList();
        return ranked[0].Score > ranked[1].Score ? ranked[0].Index : null;
    }

    // MapFunctionStart 결과 (옛 exe 마다, 옛 주소 -> 새 주소). leaf 함수는 .text 전체를 훑어야 해서 같은 대상을 다시 찾지 않게.
    // 옛 CodeImage 를 붙잡아 두지 않도록 약한 참조 표.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CodeImage, Dictionary<int, int?>> _mapStart = new();

    // 옛 함수 시작 주소 -> 대응 함수 시작. .pdata 에 없는 단순 함수는 ret 까지의 모양이 같은 첫 call 대상을 찾지 않고
    // 같은 모양의 코드 위치를 찾는다. 파일에 없는 주소(엉뚱한 call 대상)면 null.
    internal int? MapFunctionStart(CodeImage old, int oldRva)
    {
        var cache = _mapStart.GetOrCreateValue(old);
        if (cache.TryGetValue(oldRva, out var known)) return known;
        return cache[oldRva] = old._pe.FileOffset(oldRva) == null ? null : MapFunctionStartCore(old, oldRva);
    }

    private int? MapFunctionStartCore(CodeImage old, int oldRva)
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
    internal int? MapAddress(CodeImage old, int oldRva)
    {
        var pos = old.Position(oldRva);
        if (pos == null || MapFunction(old, pos.Value.Func) is not { } ni) return null;
        var rvas = Function(ni).Rvas;
        return pos.Value.Index < rvas.Length ? rvas[pos.Value.Index] : null;
    }

    // target 을 부르는(E8 rel32) 곳 중, 옛 call 위치와 뒤쪽 명령어 모양이 가장 비슷한 곳
    internal int? BestCaller(CodeImage old, int oldCallRva, int target)
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
        return (i >= 0 ? FunctionInstructions(i) : LeafInstructions(rva)).Select(x => SigRegen.Normalize(x)).ToArray();
    }

    private byte[] FunctionBytes(int index)
    {
        var (b, e) = _functions[index];
        return Slice(Offset(b), e - b);
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
