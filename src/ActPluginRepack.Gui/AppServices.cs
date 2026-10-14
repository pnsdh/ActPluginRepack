using System.IO;
using System.Text.Json;
using System.Windows;

namespace ActPluginRepack.Gui;

// 화면 설정 (%LOCALAPPDATA%\ActPluginRepack\gui.json): 진행 방식, 작업 폴더
internal sealed class GuiSettings
{
    public bool Advanced { get; set; }
    public string? Workspace { get; set; } // null 이면 사용자 작업 폴더 (%LOCALAPPDATA%\ActPluginRepack)
    public bool Hotfix { get; set; }       // 패치본 배포 기능 (HotFix 저장소에 올리는 사람만) — 고급 모드에 hotfix 옵션을 보인다

    private static string FilePath => Path.Combine(AppPaths.UserRoot, "gui.json");

    public static GuiSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save() => Files.WriteAtomic(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

// 앱 전체가 같이 쓰는 것
internal static class AppState
{
    public static GuiSettings Settings { get; } = GuiSettings.Load();

    // 새 버전 확인: 실행마다 한 번만 (네트워크라 백그라운드, 실패하면 null)
    public static Lazy<Task<AppInfo.Update?>> UpdateCheck { get; } = new(() => Task.Run(AppInfo.CheckForUpdate));

    public static Workspace Workspace
    {
        get
        {
            if (Settings.Workspace is { } dir && Directory.Exists(dir))
            {
                var ws = new Workspace(dir);
                ws.EnsureCreated();
                return ws;
            }
            return Workspace.ForUser();
        }
    }

    // 설정한 작업 폴더가 없어져서(지웠거나, 빼 둔 USB 등) 기본 폴더를 대신 쓰고 있으면 그 안내 (아니면 null).
    // 작업 폴더 입력칸은 고급 모드의 설정에만 있으므로, 간편 모드에는 설정 화면의 [기본 폴더로 되돌리기]를 안내한다.
    public static string? WorkspaceFallback =>
        Settings.Workspace is { } dir && !Directory.Exists(dir)
            ? $"설정한 작업 폴더({dir})가 없어서 기본 작업 폴더를 쓰고 있습니다. 그 폴더가 있던 드라이브(USB 등)를 다시 연결하거나, " +
              (Settings.Advanced ? "설정에서 작업 폴더를 다시 고르거나 [기본 폴더로 되돌리기]를 누르세요." : "설정에서 [기본 폴더로 되돌리기]를 누르세요.")
            : null;

    // config.json 이 JSON 문법부터 깨져 읽을 수 없는지 (게임 위치를 다시 저장할 때 옆에 보관하고 새로 만든다 — Config.MoveBroken).
    // 문법은 맞는데 내용이 틀린 것(없는 게임 경로 등)은 아니다.
    public static bool ConfigBroken()
    {
        try
        {
            string path = Workspace.ConfigPath;
            if (!File.Exists(path)) return false;
            using var _ = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return false;
        }
        catch (JsonException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // 관리자 권한 재실행에 쓰는 CLI: 배포본은 cli\ 아래(실행할 파일이 하나로 보이게), 개발 빌드는 같은 폴더
    public static string CliPath
    {
        get
        {
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "cli", "ActPluginRepack.exe"),
                Path.Combine(AppContext.BaseDirectory, "ActPluginRepack.exe"),
                Path.Combine(AppContext.BaseDirectory, "ActPluginRepack.dll"),
            ];
            return candidates.FirstOrDefault(File.Exists)
                   ?? throw new ToolException("설치에 필요한 cli\\ActPluginRepack.exe 가 없습니다. 받은 zip 을 통째로 다시 푸세요.");
        }
    }

    public static AppConfig LoadConfig() => TryLoadConfig().Config;

    // config.json 을 읽지 못하면(깨진 파일 등) 빈 설정과 그 이유. "설정이 아직 없음"(첫 실행)과 구분하는 데 쓴다.
    public static (AppConfig Config, string? Error) TryLoadConfig()
    {
        try { return (Config.Load(Workspace.Root), null); }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
        {
            return (new AppConfig([], null, null), ex is ToolException ? ex.Message : $"{Config.FileName} 파일을 읽지 못했습니다: {ex.Message}");
        }
    }
}

// Core 의 보고를 화면 스레드로 넘긴다
internal sealed class UiReporter(Action<LogKind, string> onLog, Action<string, int, int>? onProgress = null) : IReporter
{
    private readonly System.Windows.Threading.Dispatcher _ui = Application.Current.Dispatcher;

    public void Log(LogKind kind, string message) => _ui.BeginInvoke(() => onLog(kind, message));

    public void Progress(string step, int index, int count)
    {
        if (onProgress != null) _ui.BeginInvoke(() => onProgress(step, index, count));
    }
}

internal static class Runner
{
    // Core 작업을 백그라운드에서. 보고 대상과 취소 토큰은 그 작업에만 걸린다 (Operation 은 AsyncLocal).
    public static Task<T> Run<T>(IReporter reporter, CancellationToken token, Func<T> work) =>
        Task.Run(() =>
        {
            using var _ = Operation.Begin(reporter, token);
            return work();
        }, token);
}

// 오류를 컴퓨터를 잘 모르는 사람도 알아들을 말로 (제목, 설명)
internal static class Friendly
{
    public static (string Title, string Message) Describe(Exception ex) => ex switch
    {
        // 설치는 도중에 취소되지 않으므로(끝까지 하거나 시작 전) ACT 쪽은 그대로다. 준비하던 자료는 다음 실행 때 다시 확인한다.
        OperationCanceledException => ("취소했습니다", "진행하던 작업을 멈췄습니다. ACT 에 설치된 플러그인은 그대로입니다."),
        // Apps 가 비면 무엇이 켜져 있는지 모름 (관리자 권한으로 실행한 쪽이 알려 줌)
        AppsRunningException a => ("프로그램을 먼저 끄세요",
            (a.Apps.Count > 0 ? $"지금 켜져 있는 프로그램: {string.Join(", ", a.Apps)}." : "ACT 나 게임이 켜져 있습니다.") +
            " 모두 종료한 뒤 다시 누르세요. (해루봇이 켜져 있으면 해루봇도 끄세요)"),
        // 설치와 되돌리기 모두에서 생긴다
        ElevationCanceledException => ("관리자 권한이 필요한 작업을 취소했습니다",
            "아무것도 바꾸지 않았습니다. 다시 눌러서 Windows 확인 창이 뜨면 '예'를 누르세요."),
        GitHubLimitException => ("잠시 후에 다시 하세요", NetworkCause(ex)!),
        GameOutdatedException g => ("게임을 먼저 업데이트하세요",
            $"설치된 {string.Join(", ", g.Clients.Select(c => c.Region.Label))} 클라이언트가 최신 버전이 아닙니다. " +
            "런처로 로그인해서 게임 업데이트를 마친 뒤 다시 누르세요. (옛 버전 클라이언트로는 맞는 패치를 만들 수 없습니다)"),
        WorkspaceBusyException => ("이미 작업 중입니다",
            "다른 창이나 명령줄에서 같은 작업이 이미 진행 중입니다. 그 작업이 끝난 뒤에 다시 누르세요."),
        ThrottledException => ("잠시 후에 다시 하세요", NetworkCause(ex)!),
        // GitHub 이 등록된 토큰을 거절함 (api.github.com 401) — 다시 해도 같으므로 토큰을 고치게 한다
        NetworkException { Kind: NetworkFailure.TokenRejected } => ("GitHub 토큰을 확인하세요", NetworkCause(ex)!),
        NetworkException { Kind: NetworkFailure.Refused } => ("서버가 요청을 거절했습니다", NetworkCause(ex)!),
        NetworkException { Kind: NetworkFailure.ServerError } => ("서버에서 받지 못했습니다", NetworkCause(ex)!),
        NetworkException => ("인터넷에 연결할 수 없습니다", NetworkCause(ex)!),
        ToolException t => ("문제가 생겼습니다", t.Message),
        // 형식이 다른 자료 — 원본이 바뀐 것일 수도, 이 프로그램의 버그일 수도 있어 알릴 수 있게 오류 기록 파일을 같이 (진행 화면이 남긴다)
        _ when Errors.IsDataFormat(ex) => ("문제가 생겼습니다",
            $"{Errors.Describe(ex)}\n계속되면 {Errors.LogPath} 파일을 함께 알려 주세요."),
        // 파일 접근 등 흔한 실패는 그 설명을 그대로
        _ when Errors.Recoverable(ex) => ("문제가 생겼습니다", Errors.Describe(ex)),
        // 버그: 원문(영어)은 기록과 오류 기록 파일에만 둔다
        _ => ("예상하지 못한 문제가 생겼습니다", $"작업 중에 문제가 생겼습니다. 자세한 내용은 아래 기록과 {Errors.LogPath} 에 있습니다."),
    };

    // 받지 못한 까닭을 알아들을 말로 (받지 못한 것이 아니면 null). 오류 화면(Describe)과 "업데이트 정보를 다 받지 못했습니다"
    // 결과(Jobs)가 같이 쓴다.
    public static string? NetworkCause(Exception? ex) => ex switch
    {
        ThrottledException t =>
            $"{Http.ServerSubject(t.Host)} 짧은 시간에 요청이 많다고 잠시 막았습니다. 인터넷 연결은 문제없습니다. " +
            (t.RetryAfter is { } ra && ra > TimeSpan.FromMinutes(1) ? $"{Math.Ceiling(ra.TotalMinutes):0}분쯤" : "몇 분") +
            " 뒤에 다시 시도하세요. (PC방처럼 같은 인터넷을 여러 사람이 쓰면 생길 수 있습니다)",
        GitHubLimitException g =>
            "짧은 시간에 여러 번 실행해서 GitHub 의 시간당 이용 한도를 다 썼습니다. " +
            (g.Reset is { } reset ? $"{reset.ToLocalTime():HH:mm} 이후에" : "한 시간쯤 뒤에") + " 다시 시도하세요." +
            (g.Authenticated ? "" : " (고급 모드의 설정 화면에서 GitHub 토큰을 등록하면 한도가 크게 늘어납니다)"),
        // 토큰을 어디서 가져왔는지에 따라 고칠 곳이 다르다 (환경 변수가 등록한 토큰보다 먼저 쓰인다)
        NetworkException { Kind: NetworkFailure.TokenRejected } => GitHub.TokenRejectedAdvice(GitHub.Token?.Source, "고급 모드의 설정 화면에서 "),
        // 다시 해도 같은 거절 (403, 407, 410 등) — 주소·권한·프록시 문제
        NetworkException { Kind: NetworkFailure.Refused } n => WithDetail(
            $"인터넷 연결은 되지만 {Http.ServerSubject(n.Host)} 요청을 거절했습니다. 다시 해도 같습니다. 회사·학교·PC방처럼 프록시나 " +
            "방화벽이 있는 곳이면 그것이 막고 있을 수 있으니 다른 인터넷에서 해 보세요. 계속되면 이 프로그램의 새 버전이 있는지 확인하세요.", n),
        // 인터넷은 되는데 서버가 오류로 응답함
        NetworkException { Kind: NetworkFailure.ServerError } n => WithDetail(
            $"인터넷 연결은 문제없지만 {Http.ServerSubject(n.Host)} 오류로 응답했습니다. 서버 쪽 문제일 수 있으니 잠시 후 다시 시도하세요.", n),
        NetworkException => "인터넷 연결이 불안정해서 받지 못했습니다. 연결을 확인한 뒤 다시 시도하세요. 그래도 안 되면 1시간쯤 뒤에 다시 시도하세요.",
        _ => null,
    };

    // 기술적인 내용은 원문 대신 주소와 HTTP 상태만 끝에 (원문은 같은 말을 되풀이한다)
    private static string WithDetail(string message, NetworkException n) => n.Detail.Length > 0 ? $"{message}\n{n.Detail}" : message;

    // ACT 에 켜진 FFXIV 플러그인이 여러 개일 때 그 폴더들 (예외 메시지의 "직접 지정"은 화면에서 할 수 없어 목록만)
    public static string EnabledPlugins(IReadOnlyList<string>? paths) =>
        paths is { Count: > 0 } ? $"켜져 있는 플러그인: {string.Join(", ", paths)}" : "";
}
