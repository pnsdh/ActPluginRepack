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
    // false: 공식 패치 서버의 최신 버전과 비교하지 않고 설정된 클라이언트를 그대로 쓴다 (replay — 지난 버전을 다시 세울 때)
    public bool CheckLatest { get; init; } = true;
}

internal enum StepStatus { Ok, Warning, Failed }

internal sealed record StepResult(string Name, StepStatus Status, string Message)
{
    // 요약 한 줄 (CLI 와 GUI 의 prepare 요약이 같은 모양으로): "  OK   plugin                         완료했습니다."
    public string SummaryLine() => $"  {(Status == StepStatus.Ok ? "OK" : "!!"),-4} {Name,-30} {Message}";
    public LogKind SummaryKind => Status == StepStatus.Ok ? LogKind.Done : LogKind.Warn;
}

// prepare 의 결론 — 다음에 무엇을 할지는 모두 이것 하나로 정한다 (CLI 종료 코드, GUI 흐름, status, build 경고).
// .state.json 에도 남긴다 (예전 기록에는 없음 -> null).
internal enum PrepareOutcome
{
    PatchNeeded,    // 패치본을 만들 차례 (prepare 종료 코드 0)
    NotNeeded,      // 공식 플러그인이 이미 지금 게임과 맞음 (5)
    Undecided,      // 패치가 필요한지 판단하지 못함 (6)
    HooksBroken,    // 패킷 후크가 깨졌는데 고치지 못했거나, 점검 자체가 실패함 (2)
    OpcodesMissing, // opcode 를 못 구한 클라이언트가 있음 (3)
}

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
    public PrepareOutcome Outcome => !HooksOk ? PrepareOutcome.HooksBroken
        : MissingOpcodes.Count > 0 ? PrepareOutcome.OpcodesMissing
        : PatchNeeded switch { true => PrepareOutcome.PatchNeeded, false => PrepareOutcome.NotNeeded, null => PrepareOutcome.Undecided };
    // 최신 버전이 아니라 이번 재료에서 뺀 클라이언트
    public IReadOnlyList<OutdatedClient> Outdated { get; init; } = [];
    // opcode 를 못 구한 클라이언트가 있을 때, 출처를 받지 못한 까닭 (null 이면 출처는 응답했지만 아직 공개 전)
    public Exception? OpcodeDownloadFailure { get; init; }
    // 받기는 했는데 다른 까닭으로 opcode 를 쓰지 못함 (값이 겹침, 설정한 Python 이 없음 등) — 다시 해도 같을 수 있음
    public string? OpcodeProblem { get; init; }
    // 깨진 시그니처를 고치려고 후보·옛 exe 를 받다 실패한 까닭 (없으면 null — 받았는데도 고치지 못함)
    public Exception? SignatureDownloadFailure { get; init; }
    // 공개 자료 없이 정한 opcode(로컬 opcodediff / IDA / 이번 버전 것인지 확인 못 한 karashiiro) — 공개되면 다시 확인하도록 안내
    public IReadOnlyList<Region> UnconfirmedOpcodes { get; init; } = [];
    public IReadOnlyList<string> RegeneratedSignatures { get; init; } = []; // 이번에 SigRegen 으로 다시 만들어 기록한 시그니처
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
        config = CheckSettings(config, o);
        var run = new StepRunner();

        // 게임 버전 확인 — 최신 버전이 아닌 클라이언트는 이번 재료에서 뺀다
        var games = CheckGameVersions(config, run, o.CheckLatest);
        config = config with { Clients = games.Kept };

        // 1) 베이스 플러그인
        run.Stage("플러그인");
        UpdatePlugin(o, pluginPath, run);

        // 2) 스냅샷
        run.Stage("스냅샷");
        if (o.Snapshot) Snapshots(ws, config.Clients, games, run);

        // 3) 시그니처 — 깨진 게 있으면 upstream deucalion / SigRegen 으로 고쳐 보고, 기록한 게 있으면 다시 점검
        run.Stage("시그니처");
        var signatures = new SignatureStep();
        if (o.Sigcheck) CheckSignatures(ws, o, pluginPath, config.Clients, run, signatures);

        // 4) opcode
        run.Stage(OpcodeStage);
        var opcodes = o.Opcodes ? CollectOpcodes(ws, o, pluginPath, config, games, run) : null;

        // 5) CSV
        run.Stage(CsvStage);
        if (o.Csv) CollectCsv(ws, o, config, games.VersionOf, run);

        var result = Conclude(ws, o, pluginPath, config.Clients, run, games, signatures, opcodes);

        int apiRequests = GitHub.Requests - apiBefore;
        Log.Note($"[github] 이번 prepare 의 GitHub API 요청 {apiRequests}회 ({GitHub.Token?.Source ?? "토큰 없음 - 웹 페이지 먼저"})" +
                 (GitHub.Last is { } limit && apiRequests > 0 ? $", {limit.Remaining}/{limit.Limit} 남음" : ""));
        Log.Progress(DoneStage, StepRunner.Stages, StepRunner.Stages);
        return result;
    }

    // 단계 결과를 모으고 진행을 알린다. 한 단계의 실패(복구 가능한 예외)는 그 단계만 실패로 남기고 계속한다.
    private sealed class StepRunner
    {
        public const int Stages = 5;
        private int _stage;

        public List<StepResult> Results { get; } = [];
        public Exception? LastError { get; private set; }

        public void Stage(string name)
        {
            Operation.ThrowIfCancelled();
            Log.Progress(name, _stage++, Stages);
        }

        // action 이 (상태, 문구)를 돌려주면 그것으로, null 이면 "완료했습니다." 로 남긴다. 예외로 실패하면 failed 상태로
        // (이름 데이터처럼 실패해도 빌드할 수 있는 단계는 Warning). 예외 없이 끝났으면 true.
        public bool Step(string name, Func<(StepStatus Status, string Message)?> action, StepStatus failed = StepStatus.Failed)
        {
            try
            {
                var done = action();
                Results.Add(new StepResult(name, done?.Status ?? StepStatus.Ok, done?.Message ?? "완료했습니다."));
                return true;
            }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                LastError = ex;
                Log.Warn($"[{name}] 실패 - {Errors.Describe(ex)}");
                Results.Add(new StepResult(name, failed, Errors.Describe(ex)));
                return false;
            }
        }

        public bool Step(string name, Action action, StepStatus failed = StepStatus.Failed) =>
            Step(name, () => { action(); return null; }, failed);
    }

    // 설정·클라이언트·옵션 점검 (네트워크 작업 전에). --ida-target 을 반영한 설정을 돌려준다.
    private static AppConfig CheckSettings(AppConfig config, PrepareOptions o)
    {
        foreach (string e in config.SettingErrors.Values) Log.Warn($"[config] {e} - 이 항목은 없는 것으로 보고 계속");
        if (config.ClientErrors.Count > 0)
            throw new ToolException($"{Config.FileName} 의 게임 클라이언트 경로를 확인하세요:\n  " + string.Join("\n  ", config.ClientErrors));
        if (config.Clients.Count == 0)
            throw new ToolException($"게임 클라이언트가 설정되지 않았습니다 ({Config.FileName} 의 \"clients\"). 게임 설치 폴더를 지정하세요.");
        foreach (var client in config.Clients.Where(c => c.Version == null))
            throw new ToolException(File.Exists(Path.Combine(client.GameDir, "ffxivgame.ver"))
                ? $"{client.Name}: 게임 버전 파일(ffxivgame.ver)을 읽지 못했습니다. 런처가 게임을 업데이트하는 중이면 끝난 뒤 다시 실행하세요. " +
                  $"계속되면 런처로 게임 파일을 확인(복구)하세요: {client.GameDir}"
                : $"{client.Name}: 게임 버전을 읽지 못했습니다 (ffxivgame.ver 가 없음). 경로를 확인하세요: {client.ExePath}");
        if (o.IdaTarget != null) config = config with { IdaTarget = Config.ParseIdaTarget(o.IdaTarget) };
        if (o.ForceIda && config.Ida == null)
            throw new ToolException($"IDA 설치 폴더를 찾지 못했습니다. {Config.FileName} 의 \"ida\" 에 설치 폴더를 지정하세요.");
        if (o.Sources.FirstOrDefault(s => !AllSourceNames.Contains(s, StringComparer.OrdinalIgnoreCase)) is { } bad)
            throw new ToolException($"알 수 없는 출처입니다: '{bad}' (쓸 수 있는 것: {string.Join(", ", AllSourceNames)})");
        return config;
    }

    // 한 분류(CSV / opcode / 시그니처 복구)의 이름이 목록에 없으면 그 분류는 전부 쓴다
    private static HashSet<string> Pick(PrepareOptions o, string[] names) =>
        names.Any(n => o.Sources.Contains(n, StringComparer.OrdinalIgnoreCase))
            ? names.Where(n => o.Sources.Contains(n, StringComparer.OrdinalIgnoreCase)).ToHashSet()
            : names.ToHashSet();

    // Kept: 이번에 쓰는 클라이언트, Outdated: 최신 버전이 아니라 뺀 클라이언트
    // VersionOf: 클라이언트가 없거나 뺀 지역의 지금 게임 버전 (모르면 null)
    private sealed record GameCheck(List<GameClient> Kept, List<OutdatedClient> Outdated, HashSet<Region> OutdatedRegions,
                                    Func<Region, string?> VersionOf);

    // 게임 버전 확인: 공식 패치 서버의 최신 버전보다 오래된 클라이언트(한쪽 서버를 쉬느라 업데이트하지 않은 것 등)는
    // 이번 재료에서 뺀다 — 옛 exe 로 분석하면 지금 서버와 맞지 않는 opcode / 시그니처가 나온다. 업데이트는 런처
    // 로그인(이용권 확인)이 필요해 이 도구가 할 수 없다. 모두 오래됐으면 멈춘다. 확인하지 못하면 그대로 진행.
    // checkLatest 가 false 면(replay) 최신 버전을 묻지 않는다 — 모르는 것과 같아 클라이언트를 그대로 쓴다.
    private static GameCheck CheckGameVersions(AppConfig config, StepRunner run, bool checkLatest)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var region in Region.All.Where(r => checkLatest && Thaliak.Supports(r)))
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
            run.Results.Add(new StepResult(GameStepPrefix + c.Name, StepStatus.Warning,
                $"최신 버전이 아니라서 이번에는 제외했습니다 ({c.Version} < {c.Latest}). 런처로 업데이트한 뒤 다시 실행하세요."));
        }
        // 클라이언트가 없거나 뺀 지역의 지금 게임 버전: 공식 패치 서버 기준, 모르면 같은 opcode 를 쓰는 클라이언트 중 가장 새 것
        string? newestClient = kept.Where(c => c.Region!.CnOpcodes).Select(c => c.Version)
            .Max(StringComparer.Ordinal);
        // (opcode 가 다른 지역(대만판)은 다른 클라이언트 버전으로 대신하지 않는다 — 스냅샷 정리를 엉뚱한 기준으로 하지 않게)
        string? VersionOf(Region region) => latest.GetValueOrDefault(region.Name) ?? (region.CnOpcodes ? newestClient : null);
        return new GameCheck(kept, outdated, outdated.Select(c => c.Region).ToHashSet(), VersionOf);
    }

    // 1) 베이스 플러그인을 최신 공식 릴리스로. 실패해도 기존 것이 있으면 계속.
    private static void UpdatePlugin(PrepareOptions o, string pluginPath, StepRunner run)
    {
        if (o.Plugin == null && o.UpdatePlugin)
        {
            bool updated = run.Step(PluginStep, () =>
                PluginRelease.Update(Path.GetDirectoryName(pluginPath)!) is { } warning ? (StepStatus.Warning, warning) : null);
            if (!updated && !File.Exists(pluginPath) && Errors.IsDownloadFailure(run.LastError))
                throw run.LastError!; // 처음 실행인데 받지 못함 — 원인(인터넷 / 잠시 막힘 / 한도)을 그대로 알린다
        }
        if (!File.Exists(pluginPath))
            throw new ToolException("공식 플러그인을 받지 못했습니다. 인터넷 연결과 백신(격리)을 확인한 뒤 다시 시도하세요." +
                                    Errors.CliOnly($" ({pluginPath} — 직접 받아 넣고 --no-plugin 으로 실행할 수도 있습니다)"));
    }

    // 2) 스냅샷
    private static void Snapshots(Workspace ws, List<GameClient> clients, GameCheck games, StepRunner run)
    {
        foreach (var client in clients)
            run.Step($"snapshot {client.Name}", () => Snapshot(ws, client));
        // 클라이언트가 없는 지역의 폴더(IDA 가 받아 분석한 중국판 exe 등)도 지금·직전 버전만 남긴다
        foreach (var region in Region.All.Where(r => clients.All(c => c.Region != r) && !games.OutdatedRegions.Contains(r)))
        {
            // 설정된 클라이언트 버전의 폴더는 남긴다 — IDA 가 그 버전의 중국판 exe 를 받아 분석해 둔 곳이다
            try { if (games.VersionOf(region) is { } v) PruneSnapshots(ws, region, v, clients.Select(c => c.Version!).ToHashSet()); }
            catch (Exception ex) when (Errors.IsFileError(ex))
            {
                Log.Warn($@"[snapshot] snapshots\{region.Name} 정리 실패 - {Errors.Describe(ex)}");
            }
        }
    }

    // 시그니처 단계의 결과. Check: 마지막 점검 결과 (점검이 실패했거나 건너뛰었으면 null)
    // Failures: 복구 후보·옛 exe 를 받다 난 실패, Regenerated: SigRegen 으로 이 PC 에서 다시 만든 시그니처 (공개된 교체본이 아님)
    private sealed class SignatureStep
    {
        public Sigcheck.Result? Check;
        public readonly List<Exception> Failures = [];
        public readonly List<string> Regenerated = [];
    }

    // 3) 시그니처 점검 + 깨진 것 자동 복구
    private static void CheckSignatures(Workspace ws, PrepareOptions o, string pluginPath, List<GameClient> clients,
                                        StepRunner run, SignatureStep sig)
    {
        Log.Info();
        Log.Section("[sigcheck]");
        run.Step("sigcheck", () =>
        {
            var check = sig.Check = Sigcheck.Execute(pluginPath, ws.SignaturesDir, clients);
            if (check.Broken.Count > 0 && o.Repair &&
                SignatureRepair.Run(ws, clients, check, Pick(o, SignatureRepair.Sources), sig.Failures, sig.Regenerated))
            {
                Log.Info();
                Log.Section("[sigcheck] 복구 후 다시 점검");
                check = sig.Check = Sigcheck.Execute(pluginPath, ws.SignaturesDir, clients);
            }
            PruneObsoleteInputs(ws, check);
            return check.CriticalOk
                ? (StepStatus.Ok, check.OfficialHooksOk ? "후크가 정상입니다 (공식본 그대로)." : "후크가 정상입니다 (교체 시그니처 사용).")
                : (StepStatus.Failed, @"깨진 후크가 있습니다. input\signatures 를 확인하세요.");
        });
    }

    // opcode 단계의 결과
    //   Missing          opcode 를 못 구한 클라이언트
    //   DownloadFailure  그 출처를 받지 못한 까닭 (null 이면 출처는 응답했지만 아직 공개 전)
    //   Problem          받기는 했는데 다른 까닭으로 쓰지 못함
    private sealed record OpcodeStep(List<string> Missing, Exception? DownloadFailure, string? Problem,
                                     IReadOnlyDictionary<Region, OpcodeCollector.Outcome> Outcomes, List<Region> Unconfirmed);

    // 4) opcode — input\opcodes\<지역>.txt + extras\
    private static OpcodeStep CollectOpcodes(Workspace ws, PrepareOptions o, string pluginPath, AppConfig config, GameCheck games,
                                             StepRunner run)
    {
        var missing = new List<string>();
        Exception? opcodeFailure = null;
        string? opcodeProblem = null;
        using var opcodes = new OpcodeCollector(ws, pluginPath, config, o.ForceIda, o.IdaFallback, o.Overwrite, Pick(o, OpcodeCollector.SourceNames));
        foreach (var client in config.Clients)
        {
            Operation.ThrowIfCancelled();
            Log.Info();
            Log.Section($"[{client.Name}] {client.Version}  {client.ExePath}");
            OpcodeCollector.CollectResult? got = null;
            run.Step($"opcode {client.Name}", () =>
            {
                got = opcodes.Collect(client);
                // 도중에 난 예외(파일 잠금 등)는 단계 실패로 — 그 원인이 단계 결과가 된다
                if (got.Error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(got.Error);
                if (got.Outcome is { } done)
                    return (StepStatus.Ok, $"{done.Source}: 베이스 플러그인 대비 {done.Total}개 중 {done.Changed}개가 바뀌었습니다." +
                                           (done.Missing > 0 ? $" {done.Missing}개는 출처에 없어 베이스 값을 유지합니다." : ""));
                string? problem = got.DownloadFailure == null ? got.Problem : null;
                return (StepStatus.Failed, problem != null ? $"opcode 를 준비하지 못했습니다. {problem}" : OpcodeFailureText(got.DownloadFailure));
            });
            if (got!.Outcome == null)
            {
                missing.Add(client.Name);
                // 이 클라이언트의 출처를 받다 실패한 까닭으로 안내한다 (다른 단계의 네트워크 실패를 opcode 탓으로 돌리지 않게)
                opcodeFailure ??= got.DownloadFailure;
                // 단계가 예외로 실패했으면(파일 잠금 등) 그 원인을 그대로 쓴다 (앞서 적어 둔 다른 원인이나 "아직 공개 전"으로 덮지 않게)
                opcodeProblem ??= got.Error != null ? Errors.Describe(got.Error) : got.DownloadFailure == null ? got.Problem : null;
            }
        }
        run.Step("extras", () =>
        {
            var (written, skipped) = opcodes.Finish(games.VersionOf, games.OutdatedRegions);
            return (skipped.Count == 0 ? StepStatus.Ok : StepStatus.Warning,
                    string.Join(" ", written.Count > 0
                        ? skipped.Prepend($"{string.Join(", ", written.Select(w => $@"extras\{w}"))} 파일을 만들었습니다.")
                        : skipped));
        });
        return new OpcodeStep(missing, opcodeFailure, opcodeProblem, opcodes.Outcomes, [.. opcodes.Unconfirmed]);
    }

    // 5) CSV — 이름 표시용이라 실패해도 빌드는 가능 (경고)
    private static void CollectCsv(Workspace ws, PrepareOptions o, AppConfig config, Func<Region, string?> versionOf, StepRunner run)
    {
        var csv = new CsvCollector(ws, config, Pick(o, CsvCollector.SourceNames), o.Overwrite);
        foreach (var client in config.Clients)
        {
            Operation.ThrowIfCancelled();
            Log.Info();
            Log.Section($"[{client.Name}] CSV");
            run.Step($"csv {client.Name}", () => csv.Collect(client), failed: StepStatus.Warning);
        }
        run.Step("csv (cn/tc)", () => csv.CollectChinese(versionOf), failed: StepStatus.Warning);
    }

    // 패치가 필요한지 판단하고 재료 상태를 input\.state.json 에 남긴다
    private static PrepareResult Conclude(Workspace ws, PrepareOptions o, string pluginPath, List<GameClient> clients, StepRunner run,
                                          GameCheck games, SignatureStep sig, OpcodeStep? opcodes)
    {
        // 이번에 건너뛴 단계는, 같은 게임·플러그인 버전으로 한 이전 prepare 의 기록을 이어받는다
        string? pluginVersion = MaterialState.PluginVersionOf(pluginPath);
        var gameVersions = clients.Where(c => c.Region != null).ToDictionary(c => c.Region!.Name, c => c.Version!);
        var previous = MaterialState.Load(ws);
        bool sameBase = previous != null && previous.PluginVersion == pluginVersion &&
                        previous.GameVersions.Count == gameVersions.Count &&
                        gameVersions.All(kv => previous.GameVersions.GetValueOrDefault(kv.Key) == kv.Value);
        var opcodeInfo = opcodes != null
            ? opcodes.Outcomes.ToDictionary(kv => kv.Key.Name, kv => new MaterialState.OpcodeInfo(kv.Value.Source, kv.Value.Changed, kv.Value.Total, kv.Value.Missing))
            : sameBase ? previous!.Opcodes : [];
        var check = sig.Check;
        // 시그니처 점검을 했는데 실패했으면(점검 자체가 예외) 후크 상태를 모른다 — 이전 기록도 이어받지 않는다
        bool sigFailed = o.Sigcheck && check == null;
        bool? hooksOk = sigFailed ? null : check?.OfficialHooksOk ?? (sameBase ? previous!.HooksOkOfficially : null);
        bool memoryFixed = check?.MemoryFixedByInput ?? (sameBase && previous!.MemoryFixedByInput);
        var (patchNeeded, verdict) = Decide(sigFailed, hooksOk, memoryFixed,
            clients.Select(c => opcodeInfo.GetValueOrDefault(c.Region!.Name)).ToList(), pluginVersion);

        var result = new PrepareResult(run.Results, check, opcodes?.Missing ?? [], patchNeeded, verdict)
        {
            Outdated = games.Outdated,
            SignatureCheckFailed = sigFailed,
            OpcodeDownloadFailure = opcodes?.DownloadFailure,
            OpcodeProblem = opcodes?.Problem,
            SignatureDownloadFailure = Errors.DownloadFailure(sig.Failures),
            UnconfirmedOpcodes = opcodes?.Unconfirmed ?? [],
            RegeneratedSignatures = sig.Regenerated,
        };

        // opcode 단계를 건너뛰었으면 이전 기록의 것을 이어받는다 (opcodeInfo 와 같이)
        var unconfirmed = opcodes != null ? opcodes.Unconfirmed.Select(r => r.Name).ToList() : sameBase ? previous!.Unconfirmed : null;
        var outdated = games.Outdated;
        new MaterialState(DateTime.Now, pluginVersion, gameVersions, opcodeInfo, hooksOk, patchNeeded, memoryFixed,
            outdated.Count > 0 ? outdated.ToDictionary(c => c.Region.Name, c => c.Version) : null,
            unconfirmed is { Count: > 0 } ? unconfirmed : null, result.Outcome).Save(ws);
        return result;
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

    // 화면이 맞춰 쓰는 단계·진행 이름 (Jobs: 진행 막대를 나누고, 결과에 붙일 경고를 고른다)
    public const string PluginStep = "plugin", GameStepPrefix = "game ";
    public const string OpcodeStage = "opcode", CsvStage = "CSV", DoneStage = "완료";

    internal const string MissingItemsVerdict = "패치가 필요한지 판단할 수 없습니다. opcode 출처에 빠진 항목이 있습니다.";

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
            return (null, MissingItemsVerdict);
        return (false, $"공식 플러그인 {pluginVersion} 버전이 지금 게임과 이미 맞아 임시 패치가 필요 없습니다.");
    }

    // 공식이 따라잡아 필요 없어진 교체 시그니처: 자동 복구가 쓴 줄은 지우고, 직접 쓴 줄은 알린다
    private static void PruneObsoleteInputs(Workspace ws, Sigcheck.Result check)
    {
        foreach (var group in check.ObsoleteInputs.GroupBy(i => i.Kind))
        {
            string file = SignatureInput.FileFor(group.Key);
            var items = group.ToList();
            bool Covers(string inputName) => group.Key == SignatureKinds.Deucalion
                ? items.Any(i => i.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase))
                // 공통 줄(변형 없음)은 그 줄이 쓰이는 변형이 모두 따라잡았을 때만
                : SignatureInput.ExpandName(inputName, group.Key == SignatureKinds.Memory ? PluginSignatures.MemoryVariants : PluginSignatures.OodleVariants)
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
