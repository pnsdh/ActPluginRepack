// 아직 맞는 시그니처의 조용한 어긋남 검사 (prepare 의 sigdrift 단계 — 경고만, 판단은 바꾸지 않는다).
//
// sigcheck 는 맞는 곳이 있으면 "정상"으로 본다. 그런데 메모리/Oodle 시그니처와 CreateTarget 후보는 플러그인·Machina·deucalion 이
// 첫 번째 일치만 쓰므로, 패치로 맞는 곳이 늘거나 엉뚱한 곳에 맞게 되면 "정상"인 채로 잘못된 값을 읽는다 (게임은 멀쩡하고 ACT 의
// 일부 기능만 이상해지는, 원인을 찾기 가장 어려운 경우). 그래서 쓰일 시그니처마다 직전 출시 exe 와 비교한다:
//   - 가리키는 서로 다른 대상의 수가 늘었음 — 여러 곳에 맞아도 모두 같은 대상이면(ServerTime 등) 괜찮고, 대상이 갈리면 첫 일치가
//     맞는지 알 수 없다. 대상: 메모리/Oodle 은 패턴 바로 뒤에서 읽는 값(RIP 상대), CreateTarget 은 call 대상.
//   - 메모리 시그니처가 읽는 값의 영역(섹션)이 바뀌었음 — 데이터를 가리키던 것이 코드를 가리키면 다른 명령어에 맞은 것이다.
//   - 메모리 시그니처가 같은 개수로 맞지만 다른 변수를 읽음 — 패턴이 사라졌다가 다른 함수에서 다시 맞는 경우(OverlayPlugin/cactbot
//     JobGauge70 이 7.5 부터 EnvManager 를 읽게 됨). 그 변수를 쓰는 다른 함수들로 확인한다 (SigRegen 의 ReadsOtherGlobal). 두 exe 의
//     함수 지문을 만들어야 해서 클라이언트 하나에 몇 초·수백 MB 가 들므로, 결과를 직전 출시 exe 옆(sigdrift.json)에 기억해 패치마다 한 번만.
// 직전 출시에서도 맞지 않던 것(이번에 새로 만든 교체 시그니처 등)과 개수 규칙이 있는 deucalion 후크(Recv/Send/SendLobby)는 보지 않는다.
// 거짓 경보는 있을 수 있다 (늘어난 대상 중 첫 일치가 여전히 맞는 경우) — 그래서 경고만 한다.
// 직전 출시 exe 는 받지 않고 받아 둔 것만 쓴다 (prepare 의 opcode 단계 — 로컬 opcodediff — 가 받아 두므로 그 뒤에 돈다). 직전 출시의
// exe 를 패치 서버에서 받을 수 없는 시기에 prepare 를 누를 때마다 큰 패치 파일을 다시 읽지 않게.
internal static class SignatureDrift
{
    public sealed record Suspect(GameClient Client, Sigcheck.Item Item, string Why);

    // Checked: 직전 출시 exe 와 비교한 클라이언트 수
    public sealed record Result(int Checked, List<Suspect> Suspects);

    public static Result Run(Workspace ws, Sigcheck.Result check)
    {
        var suspects = new List<Suspect>();
        int checkedClients = 0;
        foreach (var group in check.InUse.GroupBy(x => x.Client))
        {
            Operation.ThrowIfCancelled();
            var client = group.Key;
            string? previous;
            try { previous = PreviousExe(ws, client); }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                Log.Note($"[sigdrift] {client.Name}: 직전 출시를 알지 못해 건너뜀 ({Errors.Describe(ex)})");
                continue;
            }
            if (previous == null)
            {
                Log.Note($"[sigdrift] {client.Name}: 받아 둔 직전 출시 exe 가 없어 건너뜀");
                continue;
            }
            checkedClients++;
            var oldExe = SigLib.Target(previous);
            var newExe = SigLib.Target(client.ExePath);
            var memo = Memo.Load(previous);
            CodeImage? oldCode = null, newCode = null; // 다른 변수 확인용 — 필요할 때만 (지문을 만들면 수백 MB)
            foreach (var (_, row) in group)
            {
                if (row.Item.Kind == SignatureKinds.Deucalion && Deucalion.RequiredMatches(row.Item.Name) != null) continue;
                var before = row.Pattern.Scan(oldExe);
                if (before.Count == 0) continue;
                var now = row.Pattern.Scan(newExe);
                string? why = Why(row.Item.Kind, oldExe, before, newExe, now);
                if (why == null && row.Item.Kind == SignatureKinds.Memory && now.Count == before.Count)
                {
                    string key = $"{client.Version}|{row.Pattern.Text}";
                    if (!memo.Results.TryGetValue(key, out why))
                    {
                        why = OtherGlobal(oldCode ??= SigLib.Run(() => new CodeImage(oldExe)), newCode ??= SigLib.Run(() => new CodeImage(newExe)),
                                          row.Pattern.Text);
                        memo.Results[key] = why;
                    }
                }
                if (why is { Length: > 0 })
                {
                    suspects.Add(new Suspect(client, row.Item, why));
                    Log.Warn($"[sigdrift] {client.Name} {Label(row.Item)} ({row.Source}): {why}");
                }
            }
            memo.Save();
        }
        if (checkedClients > 0 && suspects.Count == 0) Log.Done("[sigdrift] 쓰일 시그니처가 모두 직전 출시와 같은 모양으로 맞음");
        return new Result(checkedClients, suspects);
    }

    // 어긋남의 설명 (없으면 null). 시험에서 직접 부른다.
    internal static string? Why(string kind, ScanTarget oldExe, List<SigPattern.Match> before, ScanTarget newExe, List<SigPattern.Match> now)
    {
        if (now.Count == 0) return null; // 깨진 것은 sigcheck 가 다룬다
        int a = Targets(kind, oldExe, before), b = Targets(kind, newExe, now);
        if (b > a)
            return $"가리키는 대상이 직전 출시의 {a}곳에서 {b}곳으로 늘었습니다 (맞는 곳 {before.Count} -> {now.Count}). " +
                   "첫 번째로 맞는 곳만 쓰므로 엉뚱한 값을 읽을 수 있습니다.";
        if (kind == SignatureKinds.Memory)
        {
            string? was = Read(oldExe, before[0]).Section, sec = Read(newExe, now[0]).Section;
            if (was != null && was != ".text" && sec != was)
                return $"읽는 값이 가리키는 영역이 직전 출시와 다릅니다 ({was} -> {sec ?? "이미지 밖"}). 다른 코드에 맞았을 수 있습니다.";
        }
        return null;
    }

    // 같은 개수로 맞는 메모리 시그니처가 다른 변수를 읽는지 (그 변수를 쓰는 다른 함수들로 확인 — SigRegen). 아니면(모르면) "".
    // 확인 자체가 실패하면 경고하지 않는다 (확인 못 함은 어긋남이 아님).
    private static string OtherGlobal(CodeImage oldCode, CodeImage newCode, string pattern)
    {
        try
        {
            var r = SigRegen.Regenerate(oldCode, newCode, pattern, readOffset: 0);
            return r.ReadsOtherGlobal
                ? "직전 출시와 같은 개수로 맞지만 다른 변수를 읽습니다 (그 변수를 쓰는 다른 함수들로 확인). 다른 코드에 맞았을 수 있습니다."
                : "";
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Log.Note($"[sigdrift] 다른 변수 확인 실패: {ex.Message}");
            return "";
        }
    }

    // 다른 변수 확인 결과 기억: 직전 출시 exe 옆 sigdrift.json ("지금 버전|패턴" -> 경고 문구, 문제없으면 ""). 못 읽거나 못 쓰면 매번 확인.
    private sealed class Memo(string path, Dictionary<string, string> results)
    {
        public Dictionary<string, string> Results { get; } = results;
        private readonly int _loaded = results.Count;

        public static Memo Load(string previousExe)
        {
            string path = Path.Combine(Path.GetDirectoryName(previousExe)!, "sigdrift.json");
            try
            {
                if (File.Exists(path) &&
                    System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } loaded)
                    return new Memo(path, loaded);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || Errors.IsFileError(ex)) { }
            return new Memo(path, []);
        }

        public void Save()
        {
            if (Results.Count == _loaded) return;
            try { Files.WriteAtomic(path, System.Text.Json.JsonSerializer.Serialize(Results)); }
            catch (Exception ex) when (Errors.IsFileError(ex)) { }
        }
    }

    // 서로 다른 대상의 수 (Sigcheck.TargetOf: 메모리/Oodle 은 패턴 바로 뒤에서 읽는 값, 그 밖(CreateTarget)은 call 대상)
    private static int Targets(string kind, ScanTarget exe, List<SigPattern.Match> matches) =>
        matches.Select(m => Sigcheck.TargetOf(m, exe, kind)).Distinct().Count();

    private static SigValue Read(ScanTarget exe, SigPattern.Match m) => SigValue.Resolve(exe.Image, m, 0, rip: true);

    private static string Label(Sigcheck.Item item) =>
        item.Kind == SignatureKinds.Deucalion ? item.Name : $"{item.Kind} {item.Variant}.{item.Name}";

    // 받아 둔 직전 출시 exe: 공식 패치 서버가 있는 지역은 Thaliak 의 직전 출시, 대만판은 snapshots 에 이 PC 의 게임에서 복사해 둔
    // 이전 버전 중 가장 최근 것
    private static string? PreviousExe(Workspace ws, GameClient client)
    {
        if (client.Region == null || client.Version == null) return null;
        if (Thaliak.Supports(client.Region))
            return Thaliak.PreviousRelease(client.Region, client.Version) is { } previous
                ? Thaliak.CachedSnapshotExe(ws, client.Region, previous) : null;
        string dir = ws.SnapshotRegionDir(client.Region);
        return !Directory.Exists(dir) ? null : Directory.GetDirectories(dir)
            .Where(d => GameVersion.IsValid(Path.GetFileName(d)) && string.CompareOrdinal(Path.GetFileName(d), client.Version) < 0)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "ffxiv_dx11.exe")).FirstOrDefault(File.Exists);
    }
}
