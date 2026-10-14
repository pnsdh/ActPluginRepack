// 패치 직후 재료 준비. 중요한 것(빨리 알아야 하는 것)부터, 단계마다 실패를 격리해서:
//   0) 사전 점검  설정·클라이언트·옵션 (네트워크 작업 전에)
//      게임 버전  공식 패치 서버(Thaliak)의 최신 버전보다 오래된 클라이언트는 이번 재료에서 뺀다 (모두 오래됐으면 멈춤,
//                 확인하지 못하면 그대로 진행). 첫 네트워크 작업.
//   1) plugin    베이스 플러그인을 최신 공식 릴리스로. 실패해도 기존 것이 있으면 계속.
//   2) snapshot  옛 exe 를 공식 패치 서버(Thaliak)에서 받을 수 없는 지역(대만판)만 ffxiv_dx11.exe 를
//                snapshots\<지역>\<버전>\ 에 보관 (다음 패치 때 이전 버전과 비교용). 그리고 모든 지역에서
//                지금·직전 버전 폴더만 남기고 정리한다 (받아 둔 옛 exe, opcodediff/IDA 캐시가 들어 있음).
//   3) sigcheck  플러그인/deucalion 시그니처 점검 + 깨진 것 자동 복구 — 후크가 깨지면 아무것도 안 되므로 먼저
//   4) opcode    input\opcodes\<지역>.txt + extras\ (Lemegeton / OverlayPlugin). 출처 선택은 OpcodeCollector.
//   5) csv       게임 데이터 CSV (이름 표시용이라 실패해도 빌드는 가능). 출처 선택은 CsvCollector.
// 끝에 패치가 필요한지 판단하고(공식 플러그인이 이미 맞으면 불필요), 공식이 따라잡아 필요 없어진 자동 시그니처를
// 정리하고, 재료 상태를 input\.state.json 에 남긴다.
internal sealed record PrepareOptions
{
    public string? Plugin { get; init; }          // 베이스 플러그인 직접 지정 (지정하면 자동 갱신 안 함)
    public bool UpdatePlugin { get; init; } = true;
    public bool Snapshot { get; init; } = true;
    public bool Sigcheck { get; init; } = true;
    public bool Repair { get; init; } = true;
    public bool Opcodes { get; init; } = true;
    public bool Csv { get; init; } = true;
    public bool ForceIda { get; init; }
    public bool IdaFallback { get; init; } = true; // 다른 opcode 출처가 모두 없을 때 IDA 로 (간편 모드는 false)
    public string? IdaTarget { get; init; }        // IDA 로 분석할 exe 의 지역 (null: config.json, 기본 중국판)
    public bool Overwrite { get; init; }           // 추출 캐시 무시
    public IReadOnlyCollection<string> Sources { get; init; } = []; // 출처 제한 (비면 전부)
}

internal enum StepStatus { Ok, Warning, Failed }

internal sealed record StepResult(string Name, StepStatus Status, string Message);

internal sealed record PrepareResult(
    IReadOnlyList<StepResult> Steps,
    Sigcheck.Result? Signatures,
    IReadOnlyList<string> MissingOpcodes,   // opcode 를 못 구한 클라이언트
    bool? PatchNeeded,                      // null: 판단할 수 없음 (점검을 건너뛰었거나 실패)
    string Verdict)                         // 사람이 읽을 결론 한 줄
{
    // 시그니처 점검을 했는데 점검 자체가 실패함 (후크 상태를 모름 — 깨진 것으로 다룬다)
    public bool SignatureCheckFailed { get; init; }
    public bool HooksOk => !SignatureCheckFailed && (Signatures?.CriticalOk ?? true);
    public bool Complete => HooksOk && MissingOpcodes.Count == 0;
    // 최신 버전이 아니라 이번 재료에서 뺀 클라이언트
    public IReadOnlyList<OutdatedClient> Outdated { get; init; } = [];
    // opcode 를 못 구한 클라이언트가 있을 때, 출처를 받지 못한 까닭 (null 이면 출처는 응답했지만 아직 공개 전)
    public Exception? OpcodeDownloadFailure { get; init; }
    // 깨진 시그니처를 고치려고 후보·옛 exe 를 받다 실패한 까닭 (없으면 null — 받았는데도 고치지 못함)
    public Exception? SignatureDownloadFailure { get; init; }
    // 공개 자료 없이 직접 구한 opcode(로컬 opcodediff / IDA)로만 정한 지역 — 공개되면 다시 확인하도록 안내
    public IReadOnlyList<Region> UnconfirmedOpcodes { get; init; } = [];
}

internal sealed record OutdatedClient(string Name, Region Region, string Version, string Latest);

internal static class Prepare
{
    public static readonly string[] AllSourceNames = [.. CsvCollector.SourceNames, .. OpcodeCollector.SourceNames, .. SignatureRepair.Sources];

    public static PrepareResult Run(Workspace ws, PrepareOptions o)
    {
        string pluginPath = Path.GetFullPath(o.Plugin ?? ws.PluginPath);
        var config = Config.Load(ws.Root);
        int apiBefore = GitHub.Requests;
        Http.ForgetConnectionFailures();
        HttpCache.Bypass = o.Overwrite; // "캐시 무시하고 다시": 받은 파일도 새로 (이 작업 안에서만 — AsyncLocal)

        // 0) 사전 점검 — 받거나 바꾸기 전에 설정 문제부터
        if (config.ClientErrors.Count > 0)
            throw new ToolException($"{Config.FileName} 의 게임 클라이언트 경로를 확인하세요:\n  " + string.Join("\n  ", config.ClientErrors));
        if (config.Clients.Count == 0)
            throw new ToolException($"게임 클라이언트가 설정되지 않았습니다 ({Config.FileName} 의 \"clients\"). 게임 설치 폴더를 지정하세요.");
        foreach (var client in config.Clients.Where(c => c.Version == null))
            throw new ToolException($"{client.Name}: 게임 버전을 읽지 못했습니다 (ffxivgame.ver 가 없거나 형식이 이상함). 경로를 확인하세요: {client.ExePath}");
        if (o.IdaTarget != null) config = config with { IdaTarget = Config.ParseIdaTarget(o.IdaTarget) };
        if (o.ForceIda && config.Ida == null)
            throw new ToolException($"IDA 설치 폴더를 찾지 못했습니다. {Config.FileName} 의 \"ida\" 에 설치 폴더를 지정하세요.");
        if (o.Sources.FirstOrDefault(s => !AllSourceNames.Contains(s, StringComparer.OrdinalIgnoreCase)) is { } bad)
            throw new ToolException($"알 수 없는 출처입니다: '{bad}' (쓸 수 있는 것: {string.Join(", ", AllSourceNames)})");
        // 한 분류(CSV / opcode / 시그니처 복구)의 이름이 목록에 없으면 그 분류는 전부 쓴다
        HashSet<string> Pick(string[] names) =>
            names.Any(n => o.Sources.Contains(n, StringComparer.OrdinalIgnoreCase))
                ? names.Where(n => o.Sources.Contains(n, StringComparer.OrdinalIgnoreCase)).ToHashSet()
                : names.ToHashSet();

        var steps = new List<StepResult>();

        // 게임 버전 확인: 공식 패치 서버의 최신 버전보다 오래된 클라이언트(한쪽 서버를 쉬느라 업데이트하지 않은 것 등)는
        // 이번 재료에서 뺀다 — 옛 exe 로 분석하면 지금 서버와 맞지 않는 opcode / 시그니처가 나온다. 업데이트는 런처
        // 로그인(이용권 확인)이 필요해 이 도구가 할 수 없다. 모두 오래됐으면 멈춘다. 확인하지 못하면 그대로 진행.
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var region in Region.All.Where(Thaliak.Supports))
        {
            try
            {
                if (Thaliak.LatestVersion(region) is { } v) latest[region.Name] = v;
            }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                if (config.Clients.Any(c => c.Region == region))
                    Log.Note($"[game] {region.Name}: 최신 게임 버전을 확인하지 못함 ({Errors.Describe(ex)}) - 설치된 버전 그대로 진행");
            }
        }
        foreach (var client in config.Clients)
            if (latest.TryGetValue(client.Region!.Name, out string? newest) && Thaliak.IsOlder(newest, client.Version!))
                Log.Note($"[game] {client.Name}: 공식 패치 목록({newest})보다 새 버전 - 목록이 아직 갱신되지 않은 듯, 그대로 진행");
        var (kept, outdated) = SplitOutdated(config.Clients, latest);
        foreach (var c in outdated)
        {
            Log.Warn($"[game] {c.Name}: 게임이 최신 버전이 아님 ({c.Version} < {c.Latest}) - 이번에는 빼고 진행");
            steps.Add(new StepResult($"game {c.Name}", StepStatus.Warning,
                $"최신 버전이 아니라서 이번에는 제외했습니다 ({c.Version} < {c.Latest}). 런처로 업데이트한 뒤 다시 실행하세요."));
        }
        config = config with { Clients = kept };
        var outdatedRegions = outdated.Select(c => c.Region).ToHashSet();
        // 클라이언트가 없거나 뺀 지역의 지금 게임 버전: 공식 패치 서버 기준, 모르면 같은 opcode 를 쓰는 클라이언트 중 가장 새 것
        string? newestClient = config.Clients.Where(c => c.Region!.CnOpcodes).Select(c => c.Version)
            .Max(StringComparer.Ordinal);
        // (opcode 가 다른 지역(대만판)은 다른 클라이언트 버전으로 대신하지 않는다 — 스냅샷 정리를 엉뚱한 기준으로 하지 않게)
        string? VersionOf(Region region) => latest.GetValueOrDefault(region.Name) ?? (region.CnOpcodes ? newestClient : null);

        int stage = 0;
        const int stages = 5;
        void Stage(string name) { Operation.ThrowIfCancelled(); Log.Progress(name, stage++, stages); }

        Exception? lastError = null;
        bool Step(string name, Action action)
        {
            try
            {
                action();
                steps.Add(new StepResult(name, StepStatus.Ok, "완료했습니다."));
                return true;
            }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                lastError = ex;
                Log.Warn($"[{name}] 실패 - {Errors.Describe(ex)}");
                steps.Add(new StepResult(name, StepStatus.Failed, Errors.Describe(ex)));
                return false;
            }
        }

        // 1) 베이스 플러그인
        Stage("플러그인");
        if (o.Plugin == null && o.UpdatePlugin)
        {
            string? pluginWarning = null;
            if (Step("plugin", () => pluginWarning = PluginRelease.Update(Path.GetDirectoryName(pluginPath)!)))
            {
                if (pluginWarning != null) steps[^1] = new StepResult("plugin", StepStatus.Warning, pluginWarning);
            }
            else if (!File.Exists(pluginPath) && lastError is NetworkException or ThrottledException or GitHubLimitException)
                throw lastError; // 처음 실행인데 받지 못함 — 원인(인터넷 / 잠시 막힘 / 한도)을 그대로 알린다
        }
        if (!File.Exists(pluginPath))
            throw new ToolException($"공식 플러그인을 받지 못했습니다 ({pluginPath}). 인터넷 연결과 백신(격리)을 확인한 뒤 다시 시도하세요. (명령줄에서는 직접 받아 넣고 --no-plugin 으로 실행할 수도 있습니다)");

        // 2) 스냅샷
        Stage("스냅샷");
        if (o.Snapshot)
        {
            foreach (var client in config.Clients)
                Step($"snapshot {client.Name}", () => Snapshot(ws, client));
            // 클라이언트가 없는 지역의 폴더(IDA 가 받아 분석한 중국판 exe 등)도 지금·직전 버전만 남긴다
            foreach (var region in Region.All.Where(r => config.Clients.All(c => c.Region != r) && !outdatedRegions.Contains(r)))
            {
                // 설정된 클라이언트 버전의 폴더는 남긴다 — IDA 가 그 버전의 중국판 exe 를 받아 분석해 둔 곳이다
                try { if (VersionOf(region) is { } v) PruneSnapshots(ws, region, v, config.Clients.Select(c => c.Version!).ToHashSet()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($@"[snapshot] snapshots\{region.Name} 정리 실패 - {Errors.Describe(ex)}");
                }
            }
        }

        // 3) 시그니처 — 깨진 게 있으면 upstream deucalion / SigRegen 으로 고쳐 보고, 기록한 게 있으면 다시 점검
        Stage("시그니처");
        Sigcheck.Result? check = null;
        var sigFailures = new List<Exception>();
        if (o.Sigcheck)
        {
            Log.Info();
            Log.Section("[sigcheck]");
            if (Step("sigcheck", () =>
                {
                    check = Sigcheck.Execute(pluginPath, ws.SignaturesDir, config.Clients);
                    if (check.Broken.Count > 0 && o.Repair &&
                        SignatureRepair.Run(ws, config.Clients, check, Pick(SignatureRepair.Sources), sigFailures))
                    {
                        Log.Info();
                        Log.Section("[sigcheck] 복구 후 다시 점검");
                        check = Sigcheck.Execute(pluginPath, ws.SignaturesDir, config.Clients);
                    }
                    PruneObsoleteInputs(ws, check);
                }) && check != null)
                steps[^1] = check.CriticalOk
                    ? new StepResult("sigcheck", StepStatus.Ok, check.OfficialHooksOk ? "후크가 정상입니다 (공식본 그대로)." : "후크가 정상입니다 (교체 시그니처 사용).")
                    : new StepResult("sigcheck", StepStatus.Failed, @"깨진 후크가 있습니다. input\signatures 를 확인하세요.");
        }

        // 4) opcode
        Stage("opcode");
        var missing = new List<string>();
        Exception? opcodeFailure = null;
        OpcodeCollector? opcodes = null;
        if (o.Opcodes)
        {
            opcodes = new OpcodeCollector(ws, pluginPath, config, o.ForceIda, o.IdaFallback, o.Overwrite, Pick(OpcodeCollector.SourceNames));
            try
            {
                foreach (var client in config.Clients)
                {
                    Operation.ThrowIfCancelled();
                    Log.Info();
                    Log.Section($"[{client.Name}] {client.Version}  {client.ExePath}");
                    OpcodeCollector.Outcome? got = null;
                    Step($"opcode {client.Name}", () => got = opcodes.Collect(client));
                    if (got == null)
                    {
                        missing.Add(client.Name);
                        // 이 클라이언트의 출처를 받다 실패한 까닭으로 안내한다 (다른 단계의 네트워크 실패를 opcode 탓으로 돌리지 않게)
                        var failure = opcodes.DownloadFailure;
                        opcodeFailure ??= failure;
                        if (steps[^1].Status == StepStatus.Ok)
                            steps[^1] = new StepResult($"opcode {client.Name}", StepStatus.Failed, OpcodeFailureText(failure));
                    }
                    else steps[^1] = steps[^1] with
                    {
                        Message = $"{got.Source}: 베이스 플러그인 대비 {got.Total}개 중 {got.Changed}개가 바뀌었습니다." +
                                  (got.Missing > 0 ? $" {got.Missing}개는 출처에 없어 베이스 값을 유지합니다." : ""),
                    };
                }
                (List<string> Written, List<string> Skipped) extras = ([], []);
                if (Step("extras", () => extras = opcodes.Finish(VersionOf, outdatedRegions)))
                    steps[^1] = new StepResult("extras", extras.Skipped.Count == 0 ? StepStatus.Ok : StepStatus.Warning,
                        string.Join(" ", extras.Written.Count > 0
                            ? extras.Skipped.Prepend($"{string.Join(", ", extras.Written.Select(w => $@"extras\{w}"))} 파일을 만들었습니다.")
                            : extras.Skipped));
            }
            finally { opcodes.Dispose(); }
        }

        // 5) CSV
        Stage("CSV");
        if (o.Csv)
        {
            var csv = new CsvCollector(ws, config, Pick(CsvCollector.SourceNames), o.Overwrite);
            foreach (var client in config.Clients)
            {
                Operation.ThrowIfCancelled();
                Log.Info();
                Log.Section($"[{client.Name}] CSV");
                if (!Step($"csv {client.Name}", () => csv.Collect(client))) steps[^1] = steps[^1] with { Status = StepStatus.Warning };
            }
            if (!Step("csv (cn/tc)", () => csv.CollectChinese(VersionOf))) steps[^1] = steps[^1] with { Status = StepStatus.Warning };
        }

        // 이번에 건너뛴 단계는, 같은 게임·플러그인 버전으로 한 이전 prepare 의 기록을 이어받는다
        string? pluginVersion = MaterialState.PluginVersionOf(pluginPath);
        var gameVersions = config.Clients.Where(c => c.Region != null).ToDictionary(c => c.Region!.Name, c => c.Version!);
        var previous = MaterialState.Load(ws);
        bool sameBase = previous != null && previous.PluginVersion == pluginVersion &&
                        previous.GameVersions.Count == gameVersions.Count &&
                        gameVersions.All(kv => previous.GameVersions.GetValueOrDefault(kv.Key) == kv.Value);
        var opcodeInfo = opcodes != null
            ? opcodes.Outcomes.ToDictionary(kv => kv.Key.Name, kv => new MaterialState.OpcodeInfo(kv.Value.Source, kv.Value.Changed, kv.Value.Total, kv.Value.Missing))
            : sameBase ? previous!.Opcodes : [];
        // 시그니처 점검을 했는데 실패했으면(점검 자체가 예외) 후크 상태를 모른다 — 이전 기록도 이어받지 않는다
        bool sigFailed = o.Sigcheck && check == null;
        bool? hooksOk = sigFailed ? null : check?.OfficialHooksOk ?? (sameBase ? previous!.HooksOkOfficially : null);
        bool memoryFixed = check?.MemoryFixedByInput ?? (sameBase && previous!.MemoryFixedByInput);
        var (patchNeeded, verdict) = Decide(sigFailed, hooksOk, memoryFixed,
            config.Clients.Select(c => opcodeInfo.GetValueOrDefault(c.Region!.Name)).ToList(), pluginVersion);

        new MaterialState(DateTime.Now, pluginVersion, gameVersions, opcodeInfo, hooksOk, patchNeeded, memoryFixed,
            outdated.Count > 0 ? outdated.ToDictionary(c => c.Region.Name, c => c.Version) : null).Save(ws);

        int apiRequests = GitHub.Requests - apiBefore;
        Log.Note($"[github] 이번 prepare 의 GitHub API 요청 {apiRequests}회 ({GitHub.Token?.Source ?? "토큰 없음 - 웹 페이지 먼저"})" +
                 (GitHub.Last is { } limit && apiRequests > 0 ? $", {limit.Remaining}/{limit.Limit} 남음" : ""));
        Log.Progress("완료", stages, stages);
        return new PrepareResult(steps, check, missing, patchNeeded, verdict)
        {
            Outdated = outdated,
            SignatureCheckFailed = sigFailed,
            OpcodeDownloadFailure = opcodeFailure,
            SignatureDownloadFailure = Errors.DownloadFailure(sigFailures),
            UnconfirmedOpcodes = [.. opcodes?.Unconfirmed ?? []],
        };
    }

    // opcode 를 못 구한 단계의 설명 (failure: 출처를 받지 못한 까닭, null 이면 아직 공개 전).
    // 예외 원문은 붙이지 않는다 — 같은 말("받지 못했습니다")이 되풀이되므로 주소와 상태만.
    internal static string OpcodeFailureText(Exception? failure) => failure switch
    {
        ThrottledException t => $"opcode 를 구하지 못했습니다. {Http.ServerSubject(t.Host)} 요청을 잠시 막았으니 몇 분 뒤에 다시 실행하세요.",
        GitHubLimitException => "opcode 를 구하지 못했습니다. GitHub API 요청 한도를 다 썼으니 한도가 초기화된 뒤에 다시 실행하세요.",
        NetworkException { Kind: NetworkFailure.Connection } => "opcode 를 받지 못했습니다. 인터넷 연결을 확인한 뒤 다시 실행하세요.",
        NetworkException { Kind: NetworkFailure.TokenRejected } => $"opcode 를 받지 못했습니다. {GitHub.TokenRejectedAdvice(GitHub.Token?.Source)}",
        NetworkException { Kind: NetworkFailure.Refused } n =>
            $"opcode 를 받지 못했습니다. 서버가 요청을 거절했습니다 — 다시 해도 같으니 주소·프록시·방화벽을 확인하세요. {n.Detail}".TrimEnd(),
        NetworkException n => $"opcode 를 받지 못했습니다. 서버가 오류로 응답했으니 잠시 후 다시 실행하세요. {n.Detail}".TrimEnd(),
        _ => "opcode 를 구하지 못했습니다. 아직 공개되지 않았을 수 있으니 나중에 다시 실행하세요.",
    };

    // 클라이언트를 최신(공식 패치 서버 기준)인 것과 오래된 것으로 나눈다. 최신 버전을 모르는 지역은 그대로 둔다.
    // 모두 오래됐으면 GameOutdatedException. latest: 지역 이름 -> 최신 게임 버전
    internal static (List<GameClient> Kept, List<OutdatedClient> Outdated) SplitOutdated(
        IReadOnlyList<GameClient> clients, IReadOnlyDictionary<string, string> latest)
    {
        var kept = new List<GameClient>();
        var outdated = new List<OutdatedClient>();
        foreach (var c in clients)
        {
            if (latest.TryGetValue(c.Region!.Name, out string? newest) && Thaliak.IsOlder(c.Version!, newest))
                outdated.Add(new OutdatedClient(c.Name, c.Region, c.Version!, newest));
            else kept.Add(c);
        }
        if (kept.Count == 0 && outdated.Count > 0) throw new GameOutdatedException(outdated);
        return (kept, outdated);
    }

    // 판단: 공식 플러그인만으로 후크가 맞고, 설정된 클라이언트마다 이번 게임 버전의 opcode 26개가 베이스
    // 플러그인 값과 전부 같으면 임시 패치가 필요 없다. 하나라도 다르면 필요 — 공식 메모리 시그니처가 깨졌는데
    // 교체 시그니처로 맞는 경우도 (패치하면 그 기능이 산다). 출처에 빠진 이름이 있으면(그 이름은 베이스 값이
    // 그대로 남아 "같음"처럼 보이므로) 다른 게 없는 한 판단하지 않는다. 게임 데이터(이름 목록)는 표시용이라 뺀다.
    // 시그니처 점검이 실패했으면 다른 게 바뀌었어도 판단하지 않는다 — 후크가 깨진 채로 설치하지 않게.
    // configured: 설정된 클라이언트마다의 opcode 결과 (못 구했으면 null)
    internal static (bool? Needed, string Verdict) Decide(bool sigFailed, bool? hooksOk, bool memoryFixed,
                                                          IReadOnlyList<MaterialState.OpcodeInfo?> configured, string? pluginVersion)
    {
        if (sigFailed)
            return (null, "패치가 필요한지 판단할 수 없습니다. 시그니처 점검이 실패했습니다.");
        bool anyChanged = configured.Any(x => x is { Changed: > 0 });
        if (hooksOk == false || anyChanged || memoryFixed)
        {
            var why = new List<string>();
            if (hooksOk == false) why.Add("후크 시그니처");
            if (anyChanged) why.Add("opcode");
            if (memoryFixed) why.Add("메모리 시그니처");
            return (true, $"임시 패치가 필요합니다. {string.Join(", ", why)} 가 공식 플러그인과 다릅니다.");
        }
        if (hooksOk == null || configured.Count == 0 || configured.Any(x => x == null))
            return (null, "패치가 필요한지 판단할 수 없습니다. 점검을 건너뛰었거나 일부 데이터를 구하지 못했습니다.");
        if (configured.Any(x => x!.Missing > 0))
            return (null, "패치가 필요한지 판단할 수 없습니다. opcode 출처에 빠진 항목이 있습니다.");
        return (false, $"공식 플러그인 {pluginVersion} 버전이 지금 게임과 이미 맞아 임시 패치가 필요 없습니다.");
    }

    // 공식이 따라잡아 필요 없어진 교체 시그니처: 자동 복구가 쓴 줄은 지우고, 직접 쓴 줄은 알린다
    private static void PruneObsoleteInputs(Workspace ws, Sigcheck.Result check)
    {
        foreach (var group in check.ObsoleteInputs.GroupBy(i => i.Kind))
        {
            string file = SignatureInput.FileFor(group.Key);
            var items = group.ToList();
            bool Covers(string inputName) => group.Key == "deucalion"
                ? items.Any(i => i.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase))
                // 공통 줄(변형 없음)은 그 줄이 쓰이는 변형이 모두 따라잡았을 때만
                : SignatureInput.ExpandName(inputName, group.Key == "memory" ? PluginSignatures.MemoryVariants : PluginSignatures.OodleVariants)
                    .All(x => items.Any(i => i.Variant == x.Variant && i.Name == x.Name));
            var (removed, manual) = SignatureInput.PruneAuto(ws.SignaturesDir, file, Covers);
            if (removed.Count > 0)
                Log.Done($@"[sigcheck] 공식 플러그인이 따라잡아 필요 없어진 자동 시그니처 정리: {string.Join(", ", removed)} (input\signatures\{file})");
            if (manual.Count > 0)
                Log.Warn($@"[sigcheck] 공식 시그니처가 이미 맞는데 input\signatures\{file} 에 직접 넣은 줄이 있음: {string.Join(", ", manual)} - 필요 없으면 지울 것");
        }
    }

    private static void Snapshot(Workspace ws, GameClient client)
    {
        PruneSnapshots(ws, client.Region!, client.Version!);
        string dir = ws.SnapshotDir(client.Region!, client.Version!);
        string dst = Path.Combine(dir, Path.GetFileName(client.ExePath));
        string rel = $@"snapshots\{client.Region!.Name}\{client.Version}";
        if (Thaliak.Supports(client.Region!))
        {
            Log.Note($"[snapshot] {client.Name}: 옛 exe 는 필요할 때 공식 패치 서버에서 받으므로 보관하지 않음");
            return;
        }
        if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(client.ExePath).Length)
        {
            Log.Note($"[snapshot] 이미 있음: {rel}");
            return;
        }
        Directory.CreateDirectory(dir);
        File.Copy(client.ExePath, dst + ".tmp", overwrite: true); // 중간에 끊겨도 깨진 스냅샷이 남지 않게
        File.Move(dst + ".tmp", dst, overwrite: true);
        Log.Info($"[snapshot] {Path.GetFileName(client.ExePath)} -> {rel}");
    }

    // snapshots\<지역>\ 에서 지금 버전과 그 직전 출시 폴더만 남긴다 (비교는 직전 출시와만 하므로).
    // 직전 출시는 공식 패치 목록(Thaliak.PreviousRelease)에서, 모르면 지금보다 앞선 것 중 가장 최근 것.
    // 지금보다 새 버전 폴더(다른 설치본 등)는 건드리지 않는다.
    private static void PruneSnapshots(Workspace ws, Region region, string version, IReadOnlySet<string>? keepAlso = null)
    {
        string regionDir = ws.SnapshotRegionDir(region);
        if (!Directory.Exists(regionDir)) return;
        var older = Directory.GetDirectories(regionDir)
            .Where(d => GameVersion.IsValid(Path.GetFileName(d)))
            .Where(d => string.CompareOrdinal(Path.GetFileName(d), version) < 0)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
        string? prev = null;
        try { prev = Thaliak.PreviousRelease(region, version); }
        catch (Exception ex) when (Errors.Recoverable(ex)) { } // 모르면 가장 최근 것을 남긴다
        string? keep = prev != null && older.Any(d => Path.GetFileName(d) == prev) ? prev : older.Select(Path.GetFileName).FirstOrDefault();
        older.RemoveAll(d => Path.GetFileName(d) == keep || keepAlso?.Contains(Path.GetFileName(d)) == true);
        foreach (string d in older)
        {
            Directory.Delete(d, recursive: true);
            Log.Note($@"[snapshot] 오래된 버전 정리: snapshots\{region.Name}\{Path.GetFileName(d)}");
        }
    }
}
