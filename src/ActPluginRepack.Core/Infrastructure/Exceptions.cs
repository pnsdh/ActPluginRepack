// 예외 규칙
// - IOException 등 흔한 예외는 감싸지 않고 그대로 올린다. 사용자 말로 바꾸는 것은 Errors.Describe 가 한다.
// - 감싸서 던지는 곳은 '아무것도 바꾸지 않았다' / '되돌렸다'를 약속해야 하는 곳뿐이다 (설치, 되돌리기 등).
// - 받은 자료의 형식이 예상과 다르면 "형식이 예상과 다릅니다" 로 감싼다.

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

// ACT 에 이 도구가 받은 공식 플러그인보다 새 플러그인이 있어 그대로 둠 (해루봇의 한국 임시본 등) — 실패가 아니라 할 일이 없음
internal sealed class NewerPluginInstalledException : ToolException
{
    public NewerPluginInstalledException(string installed, string ours)
        : base($"ACT 에 더 새 플러그인({installed})이 설치되어 있어, 공식 플러그인 {ours} 버전(또는 그것으로 만든 패치본)으로 덮어쓰지 않습니다. " +
               "공식 플러그인이 갱신된 뒤 다시 실행하세요.") => (Installed, Ours) = (installed, ours);

    // 관리자 권한으로 실행한 쪽에서 났을 때 (버전은 그쪽이 남긴 문장에만 있다)
    public NewerPluginInstalledException(string message) : base(message) { }

    public string? Installed { get; }
    public string? Ours { get; }
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
// 패치 파일에 exe 가 없거나 일부만 있음 (큰 출시 묶음은 exe 가 마지막 조각이 아닌 다른 조각에 있다 — Thaliak.SnapshotExe 가 묶음의 다른 조각을 본다)
internal sealed class ExeNotInPatchException(string message) : ToolException(message);

internal sealed class ElevationCanceledException() : ToolException("관리자 권한 요청이 취소되어 아무것도 바꾸지 않았습니다.");

// 이 폴더에 쓰려면 관리자 권한으로 다시 실행해야 함
internal sealed class ElevationRequiredException(string dir)
    : Exception($"{dir} 에 쓰려면 관리자 권한이 필요합니다 (ACT 가 Program Files 안에 있는 경우)")
{
    public string Directory { get; } = dir;
}

// 같은 작업 폴더에서 다른 작업(다른 창, 명령줄)이 이미 실행 중
internal sealed class WorkspaceBusyException(string root)
    : ToolException($"이 작업 폴더({root})에서 다른 작업이 이미 실행 중입니다. 그 작업이 끝난 뒤에 다시 실행하세요.");
