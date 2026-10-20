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
// 정리하고, 재료 상태를 input\.state.json 에, 무엇을 어디서 가져왔는지(SourceReport)를 input\.sources.json 과
// extras\sources.txt 에 남긴다.
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
    // true: 설치된 게임 대신 그 지역의 공식 패치 서버(Thaliak) 최신 exe 로 (ServerRegions). 내 게임 버전은 보지 않고 CSV 는 만들지
    // 않는다 — 게임 데이터 전체가 필요해 받을 수 없으므로 (build 는 플러그인에 들어 있는 이름을 씀). 공식 패치 서버가 없는 지역
    // (대만판)의 클라이언트는 그대로 쓴다. 고급 모드·CLI 전용 (한쪽 서버만 먼저 패치됐을 때, 게임을 업데이트하기 전에, 배포용 패치본)
    public bool ServerExe { get; init; }
    public bool ServerAllRegions { get; init; } // ServerExe 에서 설치하지 않은 지역의 exe 도 받아 점검 (ServerRegions 참고)
    // true: 교차 확인 — 배포 전 점검용. opcode 는 공개본이 있어도 IDA·로컬 opcodediff 까지 모든 출처를 돌려 항목마다 견주고
    // (extras\crosscheck\ 에 출처마다의 값), 쓰일 시그니처는 공식·upstream·SigRegen·ClientStructs 로 같은 곳인지 대조한다.
    // 고르는 출처와 판단은 그대로 — 다른 것은 경고와 출처 보고로 알린다 (사람이나 Claude 가 보고 고치게). 고급 모드·CLI 전용.
    public bool CrossCheck { get; init; }
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
        var installed = config.Clients; // (공식 패치 서버 exe 로 준비하면 CheckSettings 가 빼므로 미리)
        config = CheckSettings(config, o, ws.Root, out var serverRegions);
        var run = new StepRunner();
        // 지난 출처 보고는 지운다 — 이번 prepare 가 도중에 멈추면 이미 바뀐 재료와 맞지 않는 보고가 남지 않게.
        // 교차 확인 파일도 이번에 다시 만들지 않으면 지운다
        Files.TryDeleteFile(SourceReport.PathOf(ws));
        Files.TryDeleteFile(SourceReport.TextPathOf(ws));
        if (!(o.CrossCheck && o.Opcodes)) Files.DeleteDirectoryQuietly(ws.CrossCheckDir);

        // 게임 버전 확인 — 최신 버전이 아닌 클라이언트는 이번 재료에서 뺀다 (공식 패치 서버 exe 로 준비하면 그 최신 버전)
        var games = o.ServerExe ? ServerGames(config, serverRegions, installed, run) : CheckGameVersions(config, run, o.CheckLatest);
        config = config with { Clients = games.Kept };

        // 1) 베이스 플러그인
        run.Stage("플러그인");
        UpdatePlugin(o, pluginPath, run);

        // 2) 스냅샷 (공식 패치 서버 exe 로 준비하면 여기서 받아 클라이언트로 쓴다)
        run.Stage("스냅샷");
        if (games.Server.Count > 0) config = config with { Clients = [.. FetchServerExes(ws, games.Server), .. config.Clients] };
        if (o.Snapshot) Snapshots(ws, config.Clients, games, run);

        // 3) 시그니처 — 깨진 게 있으면 upstream deucalion / SigRegen 으로 고쳐 보고, 기록한 게 있으면 다시 점검
        run.Stage("시그니처");
        var signatures = new SignatureStep();
        if (o.Sigcheck) CheckSignatures(ws, o, pluginPath, config.Clients, run, signatures);

        // 4) opcode
        run.Stage(OpcodeStage);
        var opcodes = o.Opcodes ? CollectOpcodes(ws, o, pluginPath, config, games, run) : null;
        if (o.Sigcheck) CheckDrift(ws, signatures, run);
        if (o.Sigcheck && o.CrossCheck) CrossCheckSignatures(ws, o, signatures, run);

        // 5) CSV
        run.Stage(CsvStage);
        List<SourceReport.Item>? csvSources = null, csvCross = null;
        bool csvCrossDone = false;
        if (o.Csv && o.ServerExe)
        {
            Log.Info();
            Log.Note("[csv] 공식 패치 서버의 exe 로 준비 - 게임 데이터는 받을 수 없어 건너뜀 (build 는 플러그인에 들어 있는 이름을 씀)");
            run.Results.Add(new StepResult("csv", StepStatus.Ok, "공식 패치 서버의 exe 로 준비해 건너뛰었습니다 (플러그인에 들어 있는 이름을 씁니다)."));
            csvSources = [new("", "공식 패치 서버의 exe 로 준비해 받지 않았습니다 (빌드는 플러그인에 들어 있는 이름을 씀).")];
        }
        else if (o.Csv) (csvSources, csvCross, csvCrossDone) = CollectCsv(ws, o, config, games.VersionOf, run);

        // 교차 확인 결과 (하지 않았으면 null) — 출처 보고와 재료 기록(hotfix 가 본다)에. 대조하지 못한 것(분류째로 하지 않았거나 실패,
        // 대조할 짝이 없던 항목)은 따로 — "다른 것 없음"과 섞여 다 확인한 것처럼 보이지 않게
        List<SourceReport.Item>? cross = o.CrossCheck ? [.. opcodes?.CrossCheck ?? [], .. signatures.CrossCheck ?? [], .. csvCross ?? []] : null;
        // (경고·대조하지 못한 것의 이름에는 분류를 붙인다 — opcode 줄의 이름은 지역뿐이다. 게임 데이터 줄은 이미 "CSV ko")
        var crossNamed = o.CrossCheck
            ? (opcodes?.CrossCheck ?? []).Select(i => (Item: i, Label: i.Name.Length > 0 ? $"opcode {i.Name}" : i.Text))
                .Concat((signatures.CrossCheck ?? []).Select(i => (Item: i, Label: $"시그니처 {i.Name}")))
                .Concat((csvCross ?? []).Select(i => (Item: i, Label: i.Name))).ToList()
            : [];
        List<string>? crossWarnings = o.CrossCheck ? crossNamed.Where(x => x.Item.Warning).Select(x => x.Label).ToList() : null;
        List<string>? crossGaps = null;
        if (o.CrossCheck)
        {
            crossGaps = [];
            if (opcodes?.CrossCheck == null) crossGaps.Add("opcode (하지 않았거나 실패)");
            if (signatures.CrossCheck == null) crossGaps.Add("시그니처 (하지 않았거나 실패)");
            // (공식 패치 서버 exe 로 준비하면 게임 데이터를 만들지 않아 빌드가 플러그인의 이름을 그대로 쓴다 — 대조할 것이 없다)
            if (!csvCrossDone && !o.ServerExe) crossGaps.Add("게임 데이터 (하지 않았거나 실패)");
            crossGaps.AddRange(crossNamed.Where(x => x.Item.Unchecked).Select(x => x.Label));
            if (crossGaps.Count > 0) Log.Warn($"[crosscheck] 대조하지 못한 것: {string.Join(", ", crossGaps)}");
        }
        var result = Conclude(ws, o, pluginPath, config.Clients, run, games, signatures, opcodes, crossWarnings, crossGaps);
        SaveSourceReport(ws, o, pluginPath, config.Clients, games, signatures, opcodes, csvSources, cross, result);

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
    // server: 공식 패치 서버 exe 로 준비할 때 받을 지역 (아니면 빈 것)
    private static AppConfig CheckSettings(AppConfig config, PrepareOptions o, string root, out Region[] server)
    {
        foreach (string e in config.SettingErrors.Values) Log.Warn($"[config] {e} - 이 항목은 없는 것으로 보고 계속");
        if (o.ServerAllRegions && !o.ServerExe)
            throw new ToolException("설치하지 않은 지역의 exe 도 받는 것은 공식 패치 서버 exe 로 준비할 때만 쓸 수 있습니다." +
                                    Errors.CliOnly(" (--server-exe 와 함께)"));
        server = [];
        // 공식 패치 서버 exe 로 준비하면 내 게임은 (공식 패치 서버가 없는 지역만 빼고) 쓰지 않는다
        if (o.ServerExe)
        {
            foreach (string e in config.ClientErrors) Log.Warn($"[config] {e} - 공식 패치 서버의 exe 로 준비하므로 이 클라이언트는 빼고 계속");
            // 받을 지역은 설정에 적힌 지역 전부로 — 경로가 틀린 클라이언트(게임을 옮김, 런처 업데이트 중)의 지역도 빠뜨리지 않게
            server = ServerRegions(Config.RawClients(root).Keys.Select(Region.Find).Concat(config.Clients.Select(c => c.Region)), o.ServerAllRegions);
            if (server.Length == 0)
                throw new ToolException("공식 패치 서버에서 받을 지역이 없습니다 (설치된 게임 클라이언트가 없음). 설치하지 않은 지역의 exe 도 받도록 고르세요." +
                                        Errors.CliOnly(" (--all-regions)"));
            foreach (var c in config.Clients.Where(c => !KeptWithServerExe(c)))
                Log.Note($"[game] {c.Name}: 내 게임({c.Version ?? "버전 모름"}) 대신 공식 패치 서버의 최신 exe 를 씀");
            config = config with { Clients = config.Clients.Where(KeptWithServerExe).ToList() };
        }
        else if (config.ClientErrors.Count > 0)
            throw new ToolException($"{Config.FileName} 의 게임 클라이언트 경로를 확인하세요:\n  " + string.Join("\n  ", config.ClientErrors));
        if (config.Clients.Count == 0 && !o.ServerExe)
            throw new ToolException($"게임 클라이언트가 설정되지 않았습니다 ({Config.FileName} 의 \"clients\"). 게임 설치 폴더를 지정하세요.");
        foreach (var client in config.Clients)
            if (client.Region != null && Path.GetDirectoryName(client.GameDir) is { } install && ClientLocator.InferRegion(install) is { } actual &&
                ClientLocator.Contradicts(actual, client.Region))
                Log.Warn($"[config] {client.Name} 로 적힌 클라이언트가 {actual.Name} 클라이언트로 보입니다 ({client.GameDir}) - 게임 위치 설정에서 지역을 확인하세요");
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
    // Server: 공식 패치 서버에서 받아 클라이언트로 쓸 exe 의 지역과 버전 (공식 패치 서버 exe 로 준비할 때만)
    private sealed record GameCheck(List<GameClient> Kept, List<OutdatedClient> Outdated, HashSet<Region> OutdatedRegions,
                                    Func<Region, string?> VersionOf)
    {
        public IReadOnlyList<(Region Region, string Version)> Server { get; init; } = [];
    }

    // 공식 패치 서버 exe 로 준비할 때 받는 지역: 설치된(설정된) 클라이언트의 지역. all 이면 공식 패치 서버가 있는 모든 지역
    // (글로벌·한국·중국 — 하지 않는 서버까지 확인한 배포용 패치본 등). 그때는 중국판도 들어간다: 해루봇의 중국 서버용 폴더는
    // 이 플러그인이 아니라 중국판용으로 고친 별도 플러그인(TundraWork)이라 보통은 쓸모가 없고, 중국판이 한국보다 옛 버전인
    // 시기에는 공용 시그니처(메모리·deucalion)를 그 exe 에도 맞춰야 해 복구가 막힐 수 있다. 받지 않는 지역의 opcode 는 지금처럼
    // 같은 버전의 공개 자료로 채운다. 대만판은 공식 패치 서버가 없어 설치된 클라이언트 그대로 (KeptWithServerExe).
    internal static Region[] ServerRegions(IEnumerable<Region?> installed, bool all)
    {
        var set = installed.OfType<Region>().ToHashSet();
        return Region.All.Where(Thaliak.Supports).Where(r => all || set.Contains(r)).ToArray();
    }

    // 공식 패치 서버 exe 로 준비해도 그대로 쓰는 내 클라이언트: 공식 패치 서버가 없는 지역(대만판)
    private static bool KeptWithServerExe(GameClient c) => c.Region is { } r && !Thaliak.Supports(r);

    // 공식 패치 서버에서 받은 exe 를 클라이언트처럼 (build 의 시그니처 검증도 같은 것으로 — MaterialState.ClientsUsed)
    internal static GameClient ServerClient(Region region, string exe, string version) => new(region.Name, region, exe, version);

    // 내 게임 대신 공식 패치 서버의 최신 exe 로 준비: 받을 지역(ServerRegions)의 최신 버전 (받는 것은 스냅샷 단계에서). 내 게임
    // 버전은 보지 않는다. 최신 버전을 모르는 지역이 있으면 멈춘다 — 그 지역을 빼고 만들면 시그니처를 확인하지 않은 패치본이 된다.
    // installed: 설정된 내 게임 — 공식 패치 목록보다 새 버전이면(패치 직후 목록이 아직 갱신되지 않음) 옛 exe 로 준비하게 되므로 알린다
    private static GameCheck ServerGames(AppConfig config, Region[] regions, List<GameClient> installed, StepRunner run)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var region in Region.All.Where(Thaliak.Supports))
        {
            try
            {
                if (Thaliak.LatestVersion(region) is { } v) latest[region.Name] = v;
            }
            catch (Exception ex) when (Errors.Recoverable(ex) && !regions.Contains(region)) { } // 그 지역 opcode 만 버전을 모름
        }
        var server = regions.Select(r => (Region: r, Version: latest.GetValueOrDefault(r.Name)
            ?? throw new ToolException($"공식 패치 서버(Thaliak)에서 {r.Label} 게임의 최신 버전을 찾지 못했습니다. 잠시 후 다시 실행하세요."))).ToList();
        foreach (var (region, version) in server) Log.Info($"[game] {region.Name}: 공식 패치 서버의 최신 버전 {version}");
        foreach (var c in installed)
            if (server.FirstOrDefault(s => s.Region == c.Region) is { Version: { } latestVersion } && c.Version != null && Thaliak.IsOlder(latestVersion, c.Version))
            {
                Log.Warn($"[game] {c.Name}: 공식 패치 목록({latestVersion})이 내 게임({c.Version})보다 옛 버전 - 목록이 아직 갱신되지 않은 듯");
                run.Results.Add(new StepResult(GameStepPrefix + c.Name, StepStatus.Warning,
                    $"공식 패치 서버 목록({latestVersion})이 내 게임({c.Version})보다 옛 버전이라 옛 exe 로 준비했습니다. 잠시 후 다시 실행하세요."));
            }
        string newest = server.Select(s => s.Version).Max(StringComparer.Ordinal)!;
        string? VersionOf(Region region) => latest.GetValueOrDefault(region.Name) ?? (region.CnOpcodes ? newest : null);
        return new GameCheck(config.Clients, [], [], VersionOf) { Server = server };
    }

    // 공식 패치 서버의 exe 를 snapshots\<지역>\<버전>\ 에 받아(이미 있고 서명이 맞으면 그대로) 클라이언트로 쓴다.
    // 받지 못하면 멈춘다 (ServerGames 와 같은 까닭)
    private static List<GameClient> FetchServerExes(Workspace ws, IReadOnlyList<(Region Region, string Version)> server)
    {
        var clients = new List<GameClient>();
        foreach (var (region, version) in server)
        {
            Operation.ThrowIfCancelled();
            string exe = Thaliak.SnapshotExe(ws, region, version);
            Log.Info($@"[game] {region.Name}: 공식 패치 서버의 {version} exe 를 씀 (snapshots\{region.Name}\{version})");
            clients.Add(ServerClient(region, exe, version));
        }
        return clients;
    }

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
        public List<SourceReport.Item>? CrossCheck; // 교차 확인 줄 (하지 않았으면 null)
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

    // 맞긴 맞는데 직전 출시와 다르게 맞는 시그니처 — 경고만 (판단은 그대로). 후크가 정상일 때만 (깨졌으면 그쪽이 먼저).
    // opcode 단계 뒤에 돈다: 직전 출시 exe 를 로컬 opcodediff 가 받아 두므로 (여기서는 받지 않음)
    private static void CheckDrift(Workspace ws, SignatureStep sig, StepRunner run)
    {
        if (sig.Check is not { CriticalOk: true } check) return;
        Log.Info();
        Log.Section("[sigdrift] 직전 출시 exe 와 비교");
        run.Step(DriftStep, () =>
        {
            var drift = SignatureDrift.Run(ws, check);
            return drift.Suspects.Count > 0
                ? (StepStatus.Warning, $"직전 버전과 다르게 맞는 시그니처가 {drift.Suspects.Count}개 있습니다 - ACT 의 일부 기능이 이상할 수 있습니다.")
                : (StepStatus.Ok, drift.Checked > 0 ? "직전 버전과 같은 모양으로 맞습니다." : "받아 둔 직전 버전 exe 가 없어 건너뛰었습니다.");
        }, failed: StepStatus.Warning);
    }

    // 교차 확인 단계 요약에 붙일 "대조하지 못한 것" 수 — 다른 것이 없어도 다 확인한 것처럼 읽히지 않게
    private static string UncheckedNote(int count) => count > 0 ? $", {count}개는 대조하지 못함" : "";

    // 교차 확인 (시그니처): 쓰일 시그니처를 다른 수단과 대조 — 경고만. 후크가 정상일 때만 (깨졌으면 그쪽이 먼저)
    private static void CrossCheckSignatures(Workspace ws, PrepareOptions o, SignatureStep sig, StepRunner run)
    {
        if (sig.Check is not { CriticalOk: true } check) return;
        run.Step(CrossCheckStep + " signature", () =>
        {
            // (받기 실패는 복구의 실패 안내에 섞지 않는다. 데이터 소스에서 끈 upstream / sigregen 은 여기서도 쓰지 않는다)
            var items = SignatureRepair.CrossCheck(ws, check, [], Pick(o, SignatureRepair.Sources));
            sig.CrossCheck = items.Select(i => new SourceReport.Item(i.Name, i.Summary, i.Details, i.Disagree, i.Unchecked)).ToList();
            int bad = items.Count(i => i.Disagree);
            return bad > 0
                ? (StepStatus.Warning, $"시그니처 {items.Count}개 중 {bad}개가 다른 수단과 다른 곳을 가리킵니다 - 출처 보고를 확인하세요.")
                : (StepStatus.Ok, $"시그니처 {items.Count}개를 다른 수단과 대조했습니다 (다른 곳 없음{UncheckedNote(items.Count(i => i.Unchecked))}).");
        }, failed: StepStatus.Warning);
    }

    // opcode 단계의 결과
    //   Missing          opcode 를 못 구한 클라이언트
    //   DownloadFailure  그 출처를 받지 못한 까닭 (null 이면 출처는 응답했지만 아직 공개 전)
    //   Problem          받기는 했는데 다른 까닭으로 쓰지 못함
    //   Report           출처 보고의 opcode 줄, Extras: 만든 extras 파일, CrossCheck: 교차 확인 줄 (하지 않았으면 null)
    private sealed record OpcodeStep(List<string> Missing, Exception? DownloadFailure, string? Problem,
                                     IReadOnlyDictionary<Region, OpcodeCollector.Outcome> Outcomes, List<Region> Unconfirmed,
                                     List<SourceReport.Item> Report, List<string> Extras, List<SourceReport.Item>? CrossCheck);

    // 4) opcode — input\opcodes\<지역>.txt + extras\
    private static OpcodeStep CollectOpcodes(Workspace ws, PrepareOptions o, string pluginPath, AppConfig config, GameCheck games,
                                             StepRunner run)
    {
        var missing = new List<string>();
        Exception? opcodeFailure = null;
        string? opcodeProblem = null;
        using var opcodes = new OpcodeCollector(ws, pluginPath, config, o.ForceIda, o.IdaFallback, o.Overwrite, Pick(o, OpcodeCollector.SourceNames),
                                                o.CrossCheck);
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
        var extras = new List<string>();
        run.Step("extras", () =>
        {
            var (written, skipped) = opcodes.Finish(games.VersionOf, games.OutdatedRegions);
            extras.AddRange(written.Select(w => $@"extras\{w}"));
            return (skipped.Count == 0 ? StepStatus.Ok : StepStatus.Warning,
                    string.Join(" ", written.Count > 0
                        ? skipped.Prepend($"{string.Join(", ", written.Select(w => $@"extras\{w}"))} 파일을 만들었습니다.")
                        : skipped));
        });
        // 교차 확인: 출처마다의 값을 extras\crosscheck\ 에, 출처끼리 다른 항목을 보고에 (하지 않으면 지난 것을 지워 헷갈리지 않게)
        List<SourceReport.Item>? cross = null;
        if (o.CrossCheck) run.Step(CrossCheckStep + " opcode", () =>
        {
            var results = opcodes.CrossCheckResults();
            cross = SourceReport.CrossCheckOpcodeItems(results, config.Ida != null && Pick(o, OpcodeCollector.SourceNames).Contains("ida"));
            // 이번에 쓴 클라이언트의 지역인데 대조 결과가 없으면(도중에 실패 등) 대조가 빠진 것
            foreach (var c in config.Clients.Where(c => c.Region != null && results.All(r => r.Region != c.Region)))
                cross.Add(new(c.Region!.Label, "opcode: 대조 결과가 없습니다 (출처를 구하다 실패했을 수 있습니다).", Unchecked: true));
            // 출처마다의 값 파일은 보기용 — 쓰지 못해도(탐색기가 잡고 있음 등) 대조 결과는 그대로
            try
            {
                if (opcodes.WriteCrossCheckSets(ws.CrossCheckDir).Count > 0) extras.Add($@"extras\crosscheck\ (교차 확인한 출처마다의 opcode)");
            }
            catch (Exception ex) when (Errors.IsFileError(ex)) { Log.Note($@"[crosscheck] extras\crosscheck 를 쓰지 못함 - {ex.Message}"); }
            int bad = results.Count(r => r.Differences.Count > 0);
            return bad > 0
                ? (StepStatus.Warning, $"{string.Join(", ", results.Where(r => r.Differences.Count > 0).Select(r => r.Region.Label))} opcode 에서 출처끼리 다른 항목이 있습니다 - 출처 보고를 확인하세요.")
                : (StepStatus.Ok, $"{results.Count(r => r.Sources.Count > 1)}개 지역의 opcode 를 출처끼리 대조했습니다 (다른 항목 없음{UncheckedNote(cross.Count(i => i.Unchecked))}).");
        }, failed: StepStatus.Warning);
        return new OpcodeStep(missing, opcodeFailure, opcodeProblem, opcodes.Outcomes, [.. opcodes.Unconfirmed],
                              SourceReport.OpcodeItems(opcodes.Outcomes, opcodes.Comparisons, opcodes.Unconfirmed,
                                                       config.Clients.Where(c => c.Region != null).Select(c => c.Region!).ToHashSet()),
                              extras, cross);
    }

    // 5) CSV — 이름 표시용이라 실패해도 빌드는 가능 (경고)
    // 돌려주는 것: 언어별 출처와 교차 확인 줄 (출처 보고용, 교차 확인을 하지 않으면 null), 교차 확인이 끝까지 됐는지
    private static (List<SourceReport.Item> Sources, List<SourceReport.Item>? Cross, bool CrossDone) CollectCsv(
        Workspace ws, PrepareOptions o, AppConfig config, Func<Region, string?> versionOf, StepRunner run)
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
        var sources = csv.Sources.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new SourceReport.Item(kv.Key, kv.Value)).ToList();
        if (!o.CrossCheck) return (sources, null, false);

        // 교차 확인: 설치된 클라이언트마다 모든 추출 수단·공개 저장소로 구해 이름 목록으로 견준다 (경고만)
        var cross = new List<SourceReport.Item>();
        bool done = true;
        foreach (var client in config.Clients)
        {
            Operation.ThrowIfCancelled();
            Log.Info();
            Log.Section($"[{client.Name}] CSV 교차 확인");
            done &= run.Step($"{CrossCheckStep} csv {client.Name}", () =>
            {
                var results = CsvCrossCheck.Run(ws, config, client, Pick(o, CsvCollector.SourceNames), o.Overwrite);
                foreach (var r in results)
                {
                    foreach (string d in r.Differences) Log.Warn($"[crosscheck] csv {r.Leaf}: {d}");
                    foreach (string n in r.Notes) Log.Note($"[crosscheck] csv {r.Leaf}: {n}");
                }
                var items = SourceReport.CrossCheckCsvItems(results);
                cross.AddRange(items);
                var bad = results.Where(r => r.Differences.Count > 0).Select(r => r.Leaf).ToList();
                return bad.Count > 0
                    ? (StepStatus.Warning, $"게임 데이터 {string.Join(", ", bad)} 에서 출처끼리 다른 이름이 있습니다 - 출처 보고를 확인하세요.")
                    : (StepStatus.Ok, $"게임 데이터 {string.Join(", ", results.Select(r => $"{r.Leaf}({r.Sources.Count}곳)"))} 를 출처끼리 대조했습니다 (다른 것 없음{UncheckedNote(items.Count(i => i.Unchecked))}).");
            }, failed: StepStatus.Warning);
        }
        return (sources, cross, done);
    }

    // 패치가 필요한지 판단하고 재료 상태를 input\.state.json 에 남긴다
    private static PrepareResult Conclude(Workspace ws, PrepareOptions o, string pluginPath, List<GameClient> clients, StepRunner run,
                                          GameCheck games, SignatureStep sig, OpcodeStep? opcodes, List<string>? crossWarnings,
                                          List<string>? crossGaps)
    {
        // 이번에 건너뛴 단계는, 같은 게임·플러그인 버전으로 한 이전 prepare 의 기록을 이어받는다
        string? pluginVersion = MaterialState.PluginVersionOf(pluginPath);
        var gameVersions = clients.Where(c => c.Region != null).ToDictionary(c => c.Region!.Name, c => c.Version!);
        var previous = MaterialState.Load(ws);
        bool sameBase = previous != null && previous.PluginVersion == pluginVersion && previous.FromServer == o.ServerExe &&
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
            unconfirmed is { Count: > 0 } ? unconfirmed : null, result.Outcome, o.ServerExe,
            crossWarnings, crossGaps).Save(ws);
        return result;
    }

    // 출처 보고 (input\.sources.json, extras\sources.txt) — 보여 주기용이라 쓰지 못해도 prepare 는 그대로
    private static void SaveSourceReport(Workspace ws, PrepareOptions o, string pluginPath, List<GameClient> clients, GameCheck games,
                                         SignatureStep sig, OpcodeStep? opcodes, List<SourceReport.Item>? csv, List<SourceReport.Item>? cross,
                                         PrepareResult result)
    {
        try
        {
            var gameItems = clients.Select(c => new SourceReport.Item(Region.LabelOf(c.Region),
                $"{c.Version}  " + (games.Server.Any(s => s.Region == c.Region) ? "공식 패치 서버에서 받은 exe" : $"설치된 게임 ({c.GameDir})"))).ToList();
            gameItems.AddRange(games.Outdated.Select(c => new SourceReport.Item(c.Region.Label,
                $"{c.Version}  최신({c.Latest})이 아니라 빼고 확인했습니다. 이 지역은 공개 자료로만 정했습니다.")));
            // 교체 시그니처는 점검을 건너뛰었어도 빌드가 넣으므로 늘 (점검을 하지 않았거나 실패했으면 그렇다고)
            var signatures = SourceReport.SignatureItems(ws, sig.Check?.OfficialHooksOk);
            if (!o.Sigcheck) signatures.Insert(0, new("", "이번 확인에서 점검하지 않았습니다 (빌드는 아래 교체 시그니처를 씁니다)."));
            else if (sig.Check == null) signatures.Insert(0, new("", "시그니처 점검이 실패했습니다.", Warning: true));
            new SourceReport(DateTime.Now, result.Verdict, gameItems, MaterialState.PluginVersionOf(pluginPath),
                opcodes?.Report, signatures, csv, opcodes?.Extras.Append($@"extras\{SourceReport.TextFile} (이 보고)").ToList() ?? [])
            { CrossCheck = cross }.Save(ws);
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Log.Warn($"[prepare] 출처 보고를 쓰지 못함 - {ex.Message}");
        }
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
    public const string PluginStep = "plugin", GameStepPrefix = "game ", DriftStep = "sigdrift", CrossCheckStep = "crosscheck";
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
        if (Files.SamePath(client.ExePath, dst))
        {
            Log.Note($"[snapshot] {client.Name}: 공식 패치 서버에서 받은 exe ({rel})");
            return;
        }
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
