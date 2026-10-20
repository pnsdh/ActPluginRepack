// 교차 확인 (prepare 의 교차 확인 모드 — 배포 전 점검용): 실제로 쓰일 시그니처마다 다른 수단으로도 같은 곳을 가리키는지 본다.
//   공식      같은 항목의 공식 패턴(플러그인·deucalion·Machina)이 이 exe 에서 쓰일 수 있으면 (교체 시그니처를 쓰는 경우)
//   upstream  deucalion 소스 main / 최신 릴리스의 같은 항목 (패킷 후크만). 지금 것과 글자까지 같으면 독립된 근거가 아니라 참고로만
//   SigRegen  공식 패턴이 맞던 옛 exe(직전 출시)와 이 exe 를 비교해 다시 만든 것. 그대로 맞으면 call 대상이 대응함을 확인한
//             call 형만 일치로 세고, 그 밖은 약한 근거(개수만 같음)로 따로 센다
//   ClientStructs  메모리 시그니처: 옛 exe 에서 공식 패턴이 읽던 값과 지금 읽는 값이 같은 이름인지 (글로벌만 — 주소 자료가 글로벌 기준)
// "가리키는 곳"은 자동 복구의 검증과 같은 규칙(Sigcheck.TargetOf, 메모리는 읽는 값). 판단은 바꾸지 않고 보고만 한다.
internal static partial class SignatureRepair
{
    // 시그니처 하나의 교차 확인 결과. Disagree: 다른 곳을 가리킨 수단이 있음, Unchecked: 대조할 수 있었던 수단이 없음
    // (개수만 같은 약한 근거뿐이어도 Unchecked 는 아니다 — 요약에 그렇다고 적는다)
    public sealed record CrossCheckItem(string Name, bool Disagree, bool Unchecked, string Summary, List<string> Details);

    private sealed class CrossState
    {
        public readonly List<string> Details = [];
        public int Agreed, Weak;
        public bool Disagree;
    }

    // 다시 만든 결과의 세기: 위치까지 같음 / 개수만 같음 / 위치는 견주되 같아도 약한 근거(call 대상을 확인하지 못함) / 다른 곳이라는
    // 신호 / 모름
    private enum RegenStrength { Strong, Weak, WeakCompare, Mismatch, None }

    // sources: 쓸 수단 (prepare --sources 의 upstream / sigregen — 데이터 소스에서 끈 것은 교차 확인에서도 뺀다)
    public static List<CrossCheckItem> CrossCheck(Workspace ws, Sigcheck.Result check, List<Exception> failures, IReadOnlySet<string> sources)
    {
        Log.Info();
        Log.Section("[crosscheck] 쓰일 시그니처를 다른 수단과 대조");
        List<Candidate>? upstream = null;
        var states = new Dictionary<Sigcheck.Item, CrossState>();
        // 클라이언트마다 (그 클라이언트의 지금·옛 exe 는 수백 MB 라 다 쓰면 놓는다)
        foreach (var client in check.InUse.Select(u => u.Client).Distinct())
        {
            var exes = new ExeCache();
            var exe = exes.Target(client.ExePath);
            foreach (var (_, row) in check.InUse.Where(u => u.Client == client))
            {
                Operation.ThrowIfCancelled();
                var item = row.Item;
                if (!states.TryGetValue(item, out var st)) states[item] = st = new CrossState();
                try { CheckOne(ws, exes, exe, client, row, check, st, failures, sources, ref upstream); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    st.Details.Add($"{client.Name} - 대조하지 못함 ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        var items = new List<CrossCheckItem>();
        foreach (var (item, st) in states)
        {
            string name = item.Kind == SignatureKinds.Deucalion ? item.Name : $"{item.Kind}.{item.Variant}.{item.Name}";
            bool unchecked_ = !st.Disagree && st.Agreed == 0 && st.Weak == 0;
            string summary = st.Disagree ? "다른 곳을 가리키는 수단이 있습니다."
                : st.Agreed > 0 ? $"위치까지 대조한 {st.Agreed}건이 모두 같은 곳을 가리킵니다." + (st.Weak > 0 ? $" (그 밖에 개수만 같은 것 {st.Weak}건)" : "")
                : st.Weak > 0 ? $"개수만 같습니다({st.Weak}건). 위치까지 대조한 수단은 없습니다."
                : "대조할 다른 수단이 없습니다.";
            (st.Disagree ? (Action<string>)Log.Warn : Log.Note)($"[crosscheck] {name}: {summary}");
            foreach (string d in st.Details) Log.Note($"    {d}");
            items.Add(new CrossCheckItem(name, st.Disagree, unchecked_, summary, st.Details));
        }
        return items;
    }

    private static void CheckOne(Workspace ws, ExeCache exes, ScanTarget exe, GameClient client, Sigcheck.Row row, Sigcheck.Result check,
                                 CrossState st, List<Exception> failures, IReadOnlySet<string> sources, ref List<Candidate>? upstream)
    {
        var item = row.Item;
        var now = Sigcheck.Points(row.Pattern, exe, item.Kind);
        void Compare(string what, HashSet<int>? other, string? note = null)
        {
            if (other == null)
            {
                if (note != null) st.Details.Add($"{client.Name} - {what}: {note}");
                return;
            }
            bool same = other.SetEquals(now);
            if (same) st.Agreed++;
            else st.Disagree = true;
            st.Details.Add($"{client.Name} {(same ? "=" : "≠")} {what}" +
                           (same ? $" ({now.Count}곳)" : $": 지금 {Show(now)} / {what} {Show(other)}") + (note != null ? $" - {note}" : ""));
        }

        // 공식 패턴 (교체 시그니처를 쓸 때만 — 공식을 쓰면 같은 것이라 비교할 것이 없다)
        var official = check.Official.Where(o => o.Client == client && o.Row.Item == item).Select(o => o.Row).ToList();
        var comparedOfficial = new HashSet<string>(StringComparer.Ordinal); // 이미 견준 공식 패턴 (SigRegen 이 그대로 맞으면 같은 비교라 두 번 세지 않게)
        if (row.Source == SignatureSources.Input)
            foreach (var o in official.Where(o => Sigcheck.Usable(item.Kind, item.Name, o.Pattern.Scan(exe).Count)))
            {
                Compare($"공식({o.Source})", Sigcheck.Points(o.Pattern, exe, item.Kind));
                comparedOfficial.Add(o.Pattern.Text);
            }

        // upstream deucalion (지금 것과 같은 패턴이면 독립된 근거가 아니다 — 참고로만)
        if (item.Kind == SignatureKinds.Deucalion && sources.Contains("upstream"))
        {
            upstream ??= Upstream(failures);
            if (upstream.Count == 0) st.Details.Add($"{client.Name} - upstream deucalion: 받지 못함");
            foreach (var c in upstream.Where(c => c.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (c.Pattern == row.Pattern.Text)
                {
                    st.Details.Add($"{client.Name} - {c.From}: 지금 것과 같은 패턴 (독립된 근거 아님)");
                    continue;
                }
                if (!SigPattern.TryParse(c.Pattern, out var sig))
                {
                    st.Details.Add($"{client.Name} - {c.From}: 읽을 수 없는 패턴");
                    continue;
                }
                var hits = sig!.Scan(exe);
                if (!Sigcheck.Usable(item.Kind, item.Name, hits.Count)) Compare(c.From, null, $"이 exe 에서 쓸 수 없음 ({hits.Count}곳)");
                else Compare(c.From, hits.Select(m => Sigcheck.TargetOf(m, exe, item.Kind)).ToHashSet());
            }
        }

        // SigRegen: 공식 패턴이 맞던 옛 exe 에서 다시 만들기. 공식 패턴이 여럿이면(Recv 의 deucalion 내장 / Machina 런타임)
        // 지금 exe 에서 쓰이는 것부터 — 옛 exe 를 찾은 첫 것으로
        if (official.Count > 0 && sources.Contains("sigregen"))
        {
            (HashSet<int>? Points, string? Note, RegenStrength Strength) regen = (null, null, RegenStrength.None);
            Sigcheck.Row? used = null;
            foreach (var o in official.OrderByDescending(o => Sigcheck.Usable(item.Kind, item.Name, o.Pattern.Scan(exe).Count)))
            {
                regen = Regenerated(ws, exes, client, o, failures);
                used = o;
                if (regen.Points != null || regen.Note != NoOldExe) break;
            }
            // 그대로 맞은 공식 패턴을 위에서 이미 견줬으면 같은 비교다
            if (regen.Strength is RegenStrength.Strong or RegenStrength.Weak && used != null && comparedOfficial.Contains(used.Pattern.Text))
                regen = (null, $"{regen.Note} (위 공식 패턴과 같은 비교)", RegenStrength.None);
            switch (regen.Strength)
            {
                case RegenStrength.Strong: Compare("SigRegen", regen.Points, regen.Note); break;
                case RegenStrength.WeakCompare:
                    if (regen.Points!.SetEquals(now))
                    {
                        st.Weak++;
                        st.Details.Add($"{client.Name} ~ SigRegen ({now.Count}곳): {regen.Note} (call 대상을 확인하지 못해 약한 근거)");
                    }
                    else Compare("SigRegen", regen.Points, regen.Note);
                    break;
                case RegenStrength.Weak:
                    st.Weak++;
                    st.Details.Add($"{client.Name} ~ SigRegen: {regen.Note} (개수만 같음 - 위치는 대조하지 못함)");
                    break;
                case RegenStrength.Mismatch:
                    st.Disagree = true;
                    st.Details.Add($"{client.Name} ≠ SigRegen: {regen.Note}");
                    break;
                default: Compare("SigRegen", null, regen.Note); break;
            }
        }

        // ClientStructs (메모리, 글로벌)
        if (item.Kind == SignatureKinds.Memory && client.Region?.Name == Region.Global.Name && official.FirstOrDefault() is { } o2 &&
            ClientStructsCheck(ws, exes, [client], o2, row.Pattern.Text, always: true) is var (verdict, why))
        {
            if (verdict == ClientStructsData.Verdict.Mismatch) st.Disagree = true;
            else if (verdict == ClientStructsData.Verdict.Confirmed) st.Agreed++;
            st.Details.Add($"{client.Name} {verdict switch { ClientStructsData.Verdict.Confirmed => "=", ClientStructsData.Verdict.Mismatch => "≠", _ => "-" }} ClientStructs: {why}");
        }
    }

    private static string Show(HashSet<int> points) =>
        points.Count == 0 ? "없음" : string.Join(",", points.Order().Take(4).Select(p => $"0x{p:X}")) + (points.Count > 4 ? $" 외 {points.Count - 4}곳" : "");

    private const string NoOldExe = "공식 패턴이 맞는 옛 exe 가 없음";

    // 공식 패턴이 맞던 옛 exe(받아 두었거나 직전 출시)와 이 클라이언트 exe 로 SigRegen. 가리키는 곳(못 했으면 null), 설명, 세기.
    // 그대로 맞을 때: call 형은 call 대상이 옛 대상과 대응함을 확인했으면 위치까지 같음 — 그 밖(call 형이 아닌 후크, 메모리·Oodle)은
    // 개수만 같음. SigRegen 이 공식 패턴이 "다른 곳"을 가리킨다고 알린 경우(call 대상 불일치, 읽는 자리가 달라짐, 다른 변수를 읽는데
    // 다시 만들지 못함)는 불일치 신호.
    private static (HashSet<int>? Points, string? Note, RegenStrength Strength) Regenerated(Workspace ws, ExeCache exes, GameClient client,
                                                                                           Sigcheck.Row official, List<Exception> failures)
    {
        var item = official.Item;
        string? oldExe = OldExe(ws, exes, client, official.Pattern, n => Sigcheck.Usable(item.Kind, item.Name, n), walkBack: false, failures);
        if (oldExe == null) return (null, NoOldExe, RegenStrength.None);
        int? readOffset = item.Kind is SignatureKinds.Memory or SignatureKinds.Oodle ? 0 : null;
        SigRegen.Result r;
        try { r = SigRegen.Regenerate(exes.Code(oldExe), exes.Code(client.ExePath), official.Pattern.Text, readOffset, SigLib.RegenDebug); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return (null, $"SigRegen 실패 ({ex.GetType().Name}: {ex.Message})", RegenStrength.None);
        }
        string old = Path.GetFileName(Path.GetDirectoryName(oldExe))!;
        var exe = exes.Target(client.ExePath);
        if (r.AlreadyMatches)
        {
            var points = Sigcheck.Points(official.Pattern, exe, item.Kind);
            bool call = official.Pattern.Scan(exe).Any(m => m.Saves.Count > 0);
            // 메모리·Oodle 은 읽는 자리의 모양만 확인되고 같은 변수인지는 근거가 있을 때만 확인되므로 약한 근거로 (글로벌은 ClientStructs 가 강한 근거)
            bool strong = call && !r.CallTargetUnverified;
            return (points, $"{old} 에서 그대로 맞음", strong ? RegenStrength.Strong : RegenStrength.Weak);
        }
        if (r.Pattern == null)
        {
            // 공식 패턴이 그대로 맞는데 다른 곳이라는 신호만 (SigRegen 의 설명 문구로 가린다 — 라이브러리가 따로 알리지 않으므로).
            // 다시 만들다 위치를 찾지 못한 것(Rebuild 의 "새 패턴의 call 대상이 …")은 지금 시그니처가 틀렸다는 근거가 아니다
            bool mismatch = r.ReadsOtherGlobal || r.Detail.StartsWith("새 exe 에서도", StringComparison.Ordinal) &&
                            (r.Detail.Contains("대응하지 않음", StringComparison.Ordinal) || r.Detail.Contains("읽는 자리가 달라짐", StringComparison.Ordinal));
            return (null, $"{old} 에서 다시 만들지 못함 ({r.Detail})", mismatch ? RegenStrength.Mismatch : RegenStrength.None);
        }
        var hits = SigLib.Parse(r.Pattern).Scan(exe);
        // 메모리·Oodle 은 플러그인과 같은 규칙으로 (패턴 바로 뒤를 읽음): 다시 만든 패턴의 읽는 자리(r.Offset)와, 지금 패턴이 쓰는 규칙과의
        // RIP 변위 뒤 길이 차이만 반영한다 — 지금 쪽(Sigcheck.TargetOf)과 같은 기준으로 견주게
        var regenerated = readOffset != null
            ? hits.Select(m => SigValue.Resolve(exe.Image, m, r.Offset ?? 0, rip: true, (r.RipOffset ?? 0) - (r.OldRipOffset ?? 0)).ResolvedRva
                               ?? Sigcheck.PointOf(m, item.Kind)).ToHashSet()
            : hits.Select(m => Sigcheck.TargetOf(m, exe, item.Kind)).ToHashSet();
        return (regenerated, $"{old} 에서 다시 만듦{(r.ReadsOtherGlobal ? " (공식 패턴은 다른 변수를 읽어 다시 만듦)" : "")}: {r.Pattern}",
                r.CallTargetUnverified ? RegenStrength.WeakCompare : RegenStrength.Strong);
    }
}
