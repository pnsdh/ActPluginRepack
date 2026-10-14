// 지금 상태 한눈에: 게임 버전, 베이스 플러그인, 마지막 prepare / build, ACT 에 설치된 플러그인.
// 네트워크를 쓰지 않고 파일만 읽는다 (GUI 첫 화면, CLI status).
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
    string? RestorableBackup,               // 되돌릴 수 있는 백업 (되돌린 것, 설치한 뒤로 ACT 의 플러그인이 바뀐 것은 빼고)
    IReadOnlyList<string> Advice,           // 다음에 할 일
    bool OfficialUpdate = false,            // 공식 최신본이 지금 게임과 맞는데 ACT 에는 다른 것(옛 공식본, 패치본)이 있음
    ActProblem? ActProblem = null,          // ACT 를 찾지 못한 이유의 종류 (ActError 는 그 문장)
    IReadOnlyList<string>? ActPaths = null) // 켜진 FFXIV 플러그인이 여러 개일 때 그 폴더들
{
    // 보여 줄 상태 줄 (CLI status 와 GUI 고급 모드가 같이 쓴다). 줄 배치(이름표 폭, 들여쓰기)는 화면마다, 다음에 할 일
    // (Advice)은 따로 보인다.
    public List<StatusLine> Describe()
    {
        var lines = new List<StatusLine>();
        if (ConfigError != null) lines.Add(new("설정", ConfigError, LogKind.Warn));
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
                              (InstalledIsOurs == true ? " (이 프로그램의 패치본)" : InstalledIsOurs == false ? " (패치본 아님)" : "")));
        }
        else lines.Add(new("ACT", ActError ?? "(찾지 못함)"));
        if (RestorableBackup != null) lines.Add(new("되돌릴 백업", RestorableBackup, LogKind.Note));
        return lines;
    }
}

// 상태 한 줄: 이름표(앞 줄에 이어지는 줄은 "")와 내용. Kind 는 CLI 의 출력 색.
internal sealed record StatusLine(string Label, string Text, LogKind Kind = LogKind.Info)
{
    // 고정폭 글꼴에서 한글은 두 칸 — 이름표를 width 칸에 맞춘다 (내용과 적어도 한 칸 띄움)
    public static string Pad(string label, int width) =>
        label + new string(' ', Math.Max(1, width - label.Sum(c => c is >= '\uAC00' and <= '\uD7A3' ? 2 : 1)));
}

internal static class Status
{
    public static StatusReport Collect(Workspace ws)
    {
        List<GameClient> clients = [];
        string? configError = null;
        try
        {
            var config = Config.Load(ws.Root);
            clients = config.Clients;
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
        try { act = Install.ResolveTarget(null); }
        catch (ToolException ex)
        {
            actError = ex.Message;
            if (ex is ActTargetException t) (actProblem, actPaths) = (t.Problem, t.Paths);
        }

        bool? ours = null;
        string ourDll = ws.OutputPluginPath;
        if (act != null && File.Exists(act.PluginPath) && File.Exists(ourDll))
            ours = File.ReadAllBytes(act.PluginPath).AsSpan().SequenceEqual(File.ReadAllBytes(ourDll));

        // 공식 최신본(input\plugin)과 ACT 의 플러그인이 다르고, ACT 쪽이 더 새 버전이 아니면 공식 최신본으로 바꿀 수 있다
        // (버전 번호를 바꾸지 않는 패치본은 버전이 같아도 내용이 다르다)
        bool officialUpdate = prepared?.PatchNeeded == false && stale.Count == 0 && act != null &&
                              File.Exists(act.PluginPath) && File.Exists(ws.PluginPath) &&
                              !File.ReadAllBytes(act.PluginPath).AsSpan().SequenceEqual(File.ReadAllBytes(ws.PluginPath)) &&
                              !(Version.TryParse(act.InstalledVersion, out var installed) && Version.TryParse(basePlugin, out var latest) && installed > latest);

        var advice = new List<string>();
        if (configError != null || clients.Count == 0) advice.Add($"{Config.FileName} 의 게임 클라이언트 경로를 확인하세요.");
        else if (prepared == null) advice.Add("prepare 를 실행하세요.");
        else if (stale.Count > 0) advice.Add("게임이나 플러그인이 바뀌었습니다. prepare 를 다시 실행하세요.");
        else if (prepared.PatchNeeded == false)
        {
            advice.Add("공식 플러그인이 지금 게임과 이미 맞아 임시 패치가 필요 없습니다.");
            if (officialUpdate)
                advice.Add($"ACT 의 플러그인({act!.InstalledVersion ?? "?"}{(ours == true ? ", 이 프로그램의 패치본" : "")})이 공식 최신본({basePlugin})이 아닙니다. " +
                           "install --official 로 공식 최신본을 설치하세요 (해루봇이나 ACT 에서 플러그인을 업데이트해도 됩니다).");
        }
        else if (prepared.PatchNeeded == null)
            advice.Add("마지막 prepare 에서 패치가 필요한지 판단하지 못했습니다 (일부 정보를 구하지 못함). 잠시 후 prepare 를 다시 실행하세요.");
        else if (built == null || built.BuiltAt < prepared.PreparedAt) advice.Add("build 를 실행하세요.");
        else if (ours != true) advice.Add(act != null ? "install 로 ACT 에 설치하세요." : "output\\ 의 파일을 ACT 플러그인 폴더에 복사하세요.");
        else advice.Add("설치가 끝난 상태입니다. ACT 를 실행하세요.");
        if (stale.Count == 0 && prepared != null && clients.Where(prepared.IsOutdated).Select(c => c.Name).ToList() is { Count: > 0 } skipped)
            advice.Add($"{string.Join(", ", skipped)}: 클라이언트가 최신 버전이 아니라 마지막 prepare 에서 제외했습니다. 그 서버에서 플레이할 때는 런처로 업데이트한 뒤 prepare 를 다시 실행하세요.");

        return new StatusReport(clients, configError, basePlugin, prepared, stale, built, act, actError, ours,
                                Install.RestorableBackup(ws), advice, officialUpdate, actProblem, actPaths); // 되돌릴 수 없는 백업은 보이지 않게
    }
}
