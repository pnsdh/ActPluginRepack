// 사용자가 고칠 수 있는(또는 알아야 하는) 실패. 메시지는 그대로 사용자에게 보인다.
// 화면(GUI)이 종류별로 다르게 안내할 수 있도록 흔한 경우는 하위 형식으로 나눈다.
internal class ToolException(string message) : Exception(message);

// 받지 못한 까닭: 연결 안 됨(끊김, 시간 초과) / 서버가 오류로 응답(5xx) / 서버가 요청을 거절(다시 해도 같은 4xx —
// 주소·권한·프록시 문제) / GitHub 이 토큰을 거절(401)
internal enum NetworkFailure { Connection, ServerError, Refused, TokenRejected }

// 인터넷 연결 / 서버 문제. 화면이 Kind 로 안내를 나눈다 (서버 오류를 "인터넷 연결을 확인하세요"라고 하지 않게).
// Url / Status: 안내 끝에 붙일 주소와 HTTP 상태 (모르면 null)
internal sealed class NetworkException(string message, NetworkFailure kind = NetworkFailure.Connection,
                                       string? url = null, int? status = null) : ToolException(message)
{
    public NetworkFailure Kind { get; } = kind;
    public string? Url { get; } = url;
    public int? Status { get; } = status;
    public string? Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : null;

    // 안내 끝에 붙일 "(주소: <url>, HTTP <상태>)" — 모르는 것은 빼고, 둘 다 모르면 빈 문자열
    public string Detail =>
        (Url, Status) switch
        {
            (null, null) => "",
            ({ } u, null) => $"(주소: {u})",
            (null, { } s) => $"(HTTP {s})",
            ({ } u, { } s) => $"(주소: {u}, HTTP {s})",
        };
}

// 서버가 "잠시 천천히" 하라고 막음 (HTTP 429, 또는 Retry-After 가 붙은 403). 짧으면 Http 가 기다렸다 다시 하고,
// 길거나 계속되면 이 예외. GitHub 웹 페이지 / raw 파일은 IP 당 공개되지 않은 제한이 있어 PC방처럼 IP 를 나눠 쓰면 걸릴 수 있다.
internal sealed class ThrottledException(string message, string host, TimeSpan? retryAfter) : ToolException(message)
{
    public string Host { get; } = host;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// GitHub API 요청 한도를 다 씀 (Reset 에 초기화)
internal sealed class GitHubLimitException(string message, DateTimeOffset? reset, bool authenticated) : ToolException(message)
{
    public DateTimeOffset? Reset { get; } = reset;
    public bool Authenticated { get; } = authenticated;
}

// ACT 나 게임이 켜져 있어 진행할 수 없음
internal sealed class AppsRunningException(IReadOnlyList<string> apps)
    : ToolException(apps.Count > 0 ? $"{string.Join(", ", apps)} 이(가) 실행 중입니다. 모두 종료한 뒤 다시 실행하세요."
                                   : "ACT 나 게임이 실행 중입니다. 모두 종료한 뒤 다시 실행하세요.")
{
    public IReadOnlyList<string> Apps { get; } = apps;
}

// 설정된 게임 클라이언트가 모두 최신 버전이 아님 (런처로 업데이트해야 함 — 로그인이 필요해 자동으로 못 한다)
internal sealed class GameOutdatedException(IReadOnlyList<OutdatedClient> clients)
    : ToolException("게임 클라이언트가 최신 버전이 아닙니다: " +
                    string.Join(", ", clients.Select(c => $"{c.Name} {c.Version} (최신 {c.Latest})")) +
                    ". 런처로 게임을 업데이트한 뒤 다시 실행하세요.")
{
    public IReadOnlyList<OutdatedClient> Clients { get; } = clients;
}

// ACT 의 FFXIV 플러그인 위치를 정하지 못함 — 화면이 경우마다 다르게 안내하도록 종류를 같이 준다
internal enum ActProblem { NotRegistered, MultipleEnabled, FileMissing, ConfigUnreadable, ChineseOnly }

// Paths: 켜진 것이 여러 개일 때 그 폴더들 (화면이 메시지 대신 목록만 보인다 — 메시지의 "직접 지정"은 CLI 용)
internal sealed class ActTargetException(ActProblem problem, string message, IReadOnlyList<string>? paths = null) : ToolException(message)
{
    public ActProblem Problem { get; } = problem;
    public IReadOnlyList<string> Paths { get; } = paths ?? [];
}

// 관리자 권한 확인 창에서 '아니요'
internal sealed class ElevationCanceledException() : ToolException("관리자 권한 요청이 취소되어 아무것도 바꾸지 않았습니다.");

internal static class Errors
{
    // 한 단계의 실패로 처리할 예외 — 이 밖의 것은 버그이므로 그대로 올린다
    public static bool Recoverable(Exception ex) =>
        ex is ToolException or IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException
            or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException
            or System.ComponentModel.Win32Exception or BadImageFormatException or System.Xml.XmlException;

    // 받은 자료의 형식이 예상과 다를 때 나는 예외 (버그일 수도 있다)
    public static bool IsDataFormat(Exception ex) =>
        ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException;

    // 여러 출처를 받다 난 실패 중 사용자에게 알릴 것 (막힘 > 한도 소진 > 연결 실패 > 서버 오류·거절). 받지 못한 것이 아니면 null
    public static Exception? DownloadFailure(IEnumerable<Exception> failures)
    {
        var list = failures.ToList();
        return list.OfType<ThrottledException>().FirstOrDefault() ??
               list.OfType<GitHubLimitException>().FirstOrDefault() ??
               list.OfType<NetworkException>().OrderBy(n => n.Kind == NetworkFailure.Connection ? 0 : 1).FirstOrDefault() as Exception;
    }

    // 흔한 예외를 사용자 말로
    public static string Describe(Exception ex) => ex switch
    {
        ToolException => ex.Message,
        OperationCanceledException => "작업을 취소했습니다.",
        UnauthorizedAccessException => $"파일이나 폴더에 접근할 수 없습니다. 다른 프로그램(ACT 등)이 쓰고 있거나 권한이 없을 수 있습니다. ({ex.Message})",
        FileNotFoundException f => $"파일이 없습니다: {f.FileName ?? ex.Message}",
        DirectoryNotFoundException => $"폴더를 찾지 못했습니다. ({ex.Message})",
        IOException => $"파일을 읽거나 쓰지 못했습니다. 다른 프로그램(ACT, 백신 등)이 쓰고 있을 수 있습니다. ({ex.Message})",
        BadImageFormatException => $"올바른 DLL/EXE 파일이 아닙니다. ({ex.Message})",
        _ when IsDataFormat(ex)
            => $"받은 데이터의 형식이 예상과 다릅니다. 원본 저장소의 형식이 바뀌었을 수 있습니다. ({ex.Message})",
        System.ComponentModel.Win32Exception => $"외부 프로그램을 실행하지 못했습니다. 설치되어 있는지, 경로가 맞는지 확인하세요. ({ex.Message})",
        _ => $"예상하지 못한 오류가 생겼습니다. ({ex.GetType().Name}: {ex.Message})",
    };

    // 예상 못 한 예외의 전체 내용을 남길 곳
    public static string LogPath => Path.Combine(Path.GetTempPath(), "ActPluginRepack-error.log");

    public static void WriteLog(Exception ex, string context) => WriteLog(ex.ToString(), context);

    // detail: 예외 전체 내용 (관리자 쪽에서 넘겨받은 것 등)
    public static void WriteLog(string detail, string context)
    {
        try { File.WriteAllText(LogPath, $"{DateTime.Now:O}\n{context}\n\n{detail}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
