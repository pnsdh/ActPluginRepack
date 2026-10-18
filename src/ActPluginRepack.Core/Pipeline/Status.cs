// 지금 상태 한눈에: 게임 버전, 베이스 플러그인, 마지막 prepare / build, ACT 에 설치된 플러그인.
// 네트워크를 쓰지 않고 파일만 읽는다 (GUI 첫 화면, CLI status).

// 지금 어디까지 왔는지 (앞에서부터 처음 걸리는 것). CLI 의 다음에 할 일과 GUI 첫 화면이 같은 판단을 쓴다.
internal enum StatusStage
{
    ConfigProblem,   // 게임 클라이언트 설정이 없거나 틀림
    NotPrepared,     // prepare 기록 없음
    Stale,           // prepare 뒤 게임이나 공식 플러그인이 바뀜
    NotReady,        // 마지막 prepare 가 후크가 깨진 채, 또는 opcode 를 다 구하지 못한 채 끝남 (PrepareOutcome)
    NewerInstalled,  // ACT 에 받아 둔 공식 플러그인보다 새 플러그인이 있음 (해루봇의 한국 임시본 등 — 설치는 덮어쓰지 않는다)
    OfficialMatches, // 공식 플러그인이 이미 지금 게임과 맞음 (패치 필요 없음 — OfficialUpdate 면 ACT 의 것을 바꿀 수 있음)
    Undecided,       // 패치가 필요한지 판단하지 못함
    NotBuilt,        // 마지막 prepare 뒤에 그 공식 플러그인으로 만든 결과가 없음
    NotInstalled,    // 만든 패치본이 ACT 에 없음
    Patched,         // ACT 에 지금 게임용 패치본이 설치됨
}

internal sealed record StatusReport(
    IReadOnlyList<GameClient> Clients,
    string? ConfigError,
    string? BasePluginVersion,
    MaterialState? Prepared,
    IReadOnlyList<string> StaleReasons,     // prepare 이후 바뀐 것 (비면 input\ 이 최신)
    BuildInfo? Built,
    ActTarget? Act,
    string? ActError,
    bool? InstalledIsOurs,                  // ACT 의 플러그인이 output\ 의 것과 같은지
    string? LatestBackup,                   // 아직 되돌리지 않은 가장 최근 백업 (CLI restore 가 되돌릴 것)
    IReadOnlyList<string> Advice,           // 다음에 할 일
    bool OfficialUpdate = false,            // 공식 최신본이 지금 게임과 맞는데 ACT 에는 다른 것(옛 공식본, 패치본)이 있음
    ActProblem? ActProblem = null,          // ACT 를 찾지 못한 이유의 종류 (ActError 는 그 문장)
    IReadOnlyList<string>? ActPaths = null, // 켜진 FFXIV 플러그인이 여러 개일 때 그 폴더들
    IReadOnlyList<string>? SettingErrors = null, // 값이 틀려 쓰지 않는 config.json 선택 항목
    bool PatchInstalled = false,            // ACT 에 이 도구가 설치한 패치본이 있음 (화면의 [공식 플러그인으로 되돌리기])
    StatusStage Stage = StatusStage.NotPrepared)
{
    // 보여 줄 상태 줄 (CLI status 와 GUI 고급 모드가 같이 쓴다). 줄 배치(이름표 폭, 들여쓰기)는 화면마다, 다음에 할 일
    // (Advice)은 따로 보인다.
    public List<StatusLine> Describe()
    {
        var lines = new List<StatusLine>();
        if (ConfigError != null) lines.Add(new("설정", ConfigError, LogKind.Warn));
        foreach (string e in SettingErrors ?? []) lines.Add(new("설정", $"{e} (이 항목은 쓰지 않음)", LogKind.Warn));
        foreach (var c in Clients)
            lines.Add(new("클라이언트", $"{c.Name,-20} {c.Version ?? "(버전 모름)"}" +
                                     (Prepared?.IsOutdated(c) == true ? "  (최신 버전이 아니라 prepare 에서 제외)" : "")));
        lines.Add(new("base", $"FFXIV_ACT_Plugin {BasePluginVersion ?? "(없음)"}"));
        if (Prepared is { } p)
        {
            lines.Add(new("prepare", $"{p.PreparedAt:yyyy-MM-dd HH:mm}, " +
                                     p.PatchNeeded switch { true => "패치 필요", false => "패치 불필요", null => "패치 필요 여부 판단 불가" } +
                                     $", 후크 {p.HooksOkOfficially switch { true => "공식 시그니처로 동작", false => "시그니처 교체 필요", null => "점검 안 함" }}"));
            foreach (var (region, info) in p.Opcodes) lines.Add(new("", $"opcode {region,-20} {info.Source} ({info.Changed}/{info.Total} 변경)"));
            foreach (string r in StaleReasons) lines.Add(new("", $"prepare 이후 바뀜: {r}", LogKind.Warn));
        }
        else lines.Add(new("prepare", "(기록 없음)"));
        lines.Add(new("build", Built is { } b ? $"{b.BuiltAt:yyyy-MM-dd HH:mm}, 플러그인 {b.PluginVersion}" : "(없음)"));
        if (Act is { } act)
        {
            lines.Add(new("ACT", act.PluginPath));
            lines.Add(new("", $"설치된 버전 {act.InstalledVersion ?? "?"}" +
                              (InstalledIsOurs == true ? " (이 프로그램의 패치본)"
                               : PatchInstalled ? " (이 프로그램의 예전 패치본 — 지금 output\\ 의 것과 다름)"
                               : InstalledIsOurs == false ? (act.Others.Count > 0 ? " (패치본이 아닌 폴더가 있음)" : " (패치본 아님)") : "")));
            foreach (string other in act.Others)
                lines.Add(new("", $"함께 설치: {other} ({MaterialState.PluginVersionOf(Path.Combine(other, PluginRelease.PluginFile)) ?? "?"})"));
        }
        else lines.Add(new("ACT", ActError ?? "(찾지 못함)"));
        if (LatestBackup != null) lines.Add(new("마지막 백업", LatestBackup, LogKind.Note));
        return lines;
    }
}

// 상태 한 줄: 이름표(앞 줄에 이어지는 줄은 "")와 내용. Kind 는 CLI 의 출력 색.
internal sealed record StatusLine(string Label, string Text, LogKind Kind = LogKind.Info)
{
    // 고정폭 글꼴에서 한글은 두 칸 — 이름표를 width 칸에 맞춘다 (내용과 적어도 한 칸 띄움)
    public static string Pad(string label, int width) =>
        label + new string(' ', Math.Max(1, width - label.Sum(c => c is >= '\uAC00' and <= '\uD7A3' ? 2 : 1)));

    // 이름표를 width 칸에 맞춘 한 줄 (CLI 의 status 와 GUI 의 상태 칸이 같은 모양으로)
    public const int LabelWidth = 12;
    public string Format(int width = LabelWidth) => Format(Label, Text, width);
    public static string Format(string label, string text, int width = LabelWidth) => Pad(label, width) + text;
}

internal static class Status
{
    public static StatusReport Collect(Workspace ws)
    {
        List<GameClient> clients = [];
        string? configError = null;
        IReadOnlyList<string> settingErrors = [];
        try
        {
            var config = Config.Load(ws.Root);
            clients = config.Clients;
            settingErrors = config.SettingErrors.Values.ToList();
            if (config.ClientErrors.Count > 0) configError = string.Join(" / ", config.ClientErrors);
        }
        catch (ToolException ex) { configError = ex.Message; }

        string? basePlugin = MaterialState.PluginVersionOf(ws.PluginPath);
        var prepared = MaterialState.Load(ws);
        var stale = prepared?.Differences(ws.PluginPath, clients) ?? [];
        var built = BuildInfo.Load(ws.OutputDir);

        ActTarget? act = null;
        string? actError = null;
        ActProblem? actProblem = null;
        IReadOnlyList<string>? actPaths = null;
        try { act = ActLocator.ResolveTarget(null); }
        catch (ToolException ex)
        {
            actError = ex.Message;
            if (ex is ActTargetException t) (actProblem, actPaths) = (t.Problem, t.Paths);
        }

        // 해루봇의 언어별 폴더가 여럿이면 모두 (설치도 모두에 한다 — 더 새 플러그인이 든 다른 언어 폴더는 설치할 때 빼므로 여기서도)
        var actPlugins = act?.PluginPaths.Where(File.Exists).ToList() ?? [];
        static bool Same(string a, string b) => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        // 설치와 같은 판단으로: ACT 에 output\ 의 패치본이 그대로 있는지, ACT 의 플러그인이 받아 둔 공식본보다 새 것인지
        bool? ours = actPlugins.Count > 0 && File.Exists(ws.OutputPluginPath) ? Install.PatchInPlace(ws, act!) : null;
        bool newer = act != null && File.Exists(ws.PluginPath) && Install.NewerPluginIn(act, ws.PluginPath) != null;

        // 공식 최신본(input\plugin)과 ACT 의 플러그인이 다르고, ACT 쪽이 더 새 버전이 아니면 공식 최신본으로 바꿀 수 있다
        // (버전 번호를 바꾸지 않는 패치본은 버전이 같아도 내용이 다르다)
        bool officialUpdate = prepared?.PatchNeeded == false && stale.Count == 0 && act != null && !newer &&
                              File.Exists(act.PluginPath) && File.Exists(ws.PluginPath) &&
                              actPlugins.Any(f => !Same(f, ws.PluginPath) && !Versions.IsNewer(f, ws.PluginPath));
        bool patchInstalled = act != null && (ours == true || Install.PatchInstalled(ws, act)); // 백업이 정리됐어도 output\ 의 것이면

        var stage = configError != null || clients.Count == 0 ? StatusStage.ConfigProblem
            : prepared == null ? StatusStage.NotPrepared
            : stale.Count > 0 ? StatusStage.Stale
            : newer ? StatusStage.NewerInstalled
            : prepared.Outcome is PrepareOutcome.HooksBroken or PrepareOutcome.OpcodesMissing ? StatusStage.NotReady
            : prepared.PatchNeeded == false ? StatusStage.OfficialMatches
            : prepared.PatchNeeded == null ? StatusStage.Undecided
            // (게임이 또 업데이트돼 다시 확인했는데 실패했으면, 남아 있는 결과는 이전 게임 버전용이다)
            : built?.IsCurrentFor(prepared) != true ? StatusStage.NotBuilt
            : ours != true ? StatusStage.NotInstalled
            : StatusStage.Patched;

        var advice = new List<string>();
        switch (stage)
        {
            case StatusStage.ConfigProblem: advice.Add($"{Config.FileName} 의 게임 클라이언트 경로를 확인하세요."); break;
            case StatusStage.NotPrepared: advice.Add("prepare 를 실행하세요."); break;
            case StatusStage.Stale: advice.Add("게임이나 플러그인이 바뀌었습니다. prepare 를 다시 실행하세요."); break;
            case StatusStage.NotReady:
                advice.Add(prepared!.Outcome == PrepareOutcome.HooksBroken
                    ? "마지막 prepare 에서 패킷 후크가 깨진 채 끝났습니다 (자동으로 고치지 못했거나 점검이 실패함). 이대로 build 하면 패킷을 받지 못합니다 — " +
                      "input\\signatures 를 확인하거나 잠시 후 prepare 를 다시 실행하세요."
                    : "마지막 prepare 에서 opcode 를 다 구하지 못했습니다. 아직 공개 전일 수 있으니 잠시 후 prepare 를 다시 실행하세요.");
                break;
            case StatusStage.NewerInstalled:
                advice.Add($"ACT 의 플러그인({act!.InstalledVersion ?? "?"})이 받아 둔 공식 플러그인({basePlugin})보다 새 버전이라 install 은 덮어쓰지 않습니다 " +
                           "(해루봇의 한국 임시본 등). 공식 플러그인이 갱신되면 prepare 를 다시 실행하세요.");
                break;
            case StatusStage.OfficialMatches:
                advice.Add("공식 플러그인이 지금 게임과 이미 맞아 임시 패치가 필요 없습니다.");
                if (officialUpdate)
                    advice.Add($"ACT 의 플러그인({act!.InstalledVersion ?? "?"}{(patchInstalled ? ", 이 프로그램의 패치본" : "")})이 공식 최신본({basePlugin})이 아닙니다. " +
                               "install --official 로 공식 최신본을 설치하세요 (해루봇이나 ACT 에서 플러그인을 업데이트해도 됩니다).");
                break;
            case StatusStage.Undecided:
                advice.Add("마지막 prepare 에서 패치가 필요한지 판단하지 못했습니다 (일부 정보를 구하지 못함). 잠시 후 prepare 를 다시 실행하세요.");
                break;
            case StatusStage.NotBuilt: advice.Add("build 를 실행하세요."); break;
            case StatusStage.NotInstalled: advice.Add(act != null ? "install 로 ACT 에 설치하세요." : "output\\ 의 파일을 ACT 플러그인 폴더에 복사하세요."); break;
            default: advice.Add("설치가 끝난 상태입니다. ACT 를 실행하세요."); break;
        }
        if (stale.Count == 0 && prepared != null && clients.Where(prepared.IsOutdated).Select(c => c.Name).ToList() is { Count: > 0 } skipped)
            advice.Add($"{string.Join(", ", skipped)}: 클라이언트가 최신 버전이 아니라 마지막 prepare 에서 제외했습니다. 그 서버에서 플레이할 때는 런처로 업데이트한 뒤 prepare 를 다시 실행하세요.");

        return new StatusReport(clients, configError, basePlugin, prepared, stale, built, act, actError, ours,
                                Install.LatestBackup(ws), advice, officialUpdate, actProblem, actPaths,
                                settingErrors, patchInstalled, stage);
    }
}
