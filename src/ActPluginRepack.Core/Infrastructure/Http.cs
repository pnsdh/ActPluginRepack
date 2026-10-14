using System.Text.Json;

// HTTP GET. 404 면 null, 그 외 실패는 ToolException (네트워크 / 시간 초과 / 요청 제한 안내).
// api.github.com 요청에는 GitHub 토큰을 붙인다 (GitHub.cs — 비인증 한도는 IP 당 시간당 60회).
// 현재 작업(Operation)이 취소되면 OperationCanceledException (다시 시도를 기다리는 중에도).
//
// 다시 시도: 서버 오류(500/502/503/504), "잠시 천천히"(429, Retry-After 가 붙은 403)는 최대 3번 시도한다 — 다시 시도는
// 2번, 2초 / 5초 뒤(Retry-After 가 있으면 그만큼). 연결 실패(연결 안 됨, 응답 헤더가 30초 안에 안 옴)는 2초 뒤 1번만 다시
// 하고(2번 시도), 본문을 받다 끊기면 3초 뒤 1번만 다시 받는다. 서버가 10초보다 오래 기다리라고 하면 기다리지 않고 바로
// 실패로 넘긴다 — 그 사이 화면이 멈춘 것처럼 보이지 않게, 그리고 다른 출처로 넘어갈 수 있게. 막힌 경우는
// ThrottledException 으로 구분해 "잠시 후 다시"라고 안내한다(인터넷 연결 문제가 아님). GitHub API 의 시간당 한도 소진은 다시 해도 소용없어 바로 GitHubLimitException.
// 그 밖의 실패는 NetworkException 이고 Kind 로 연결 실패 / 서버 오류 응답(5xx) / 거절(다시 해도 같은 4xx) /
// 토큰 거절(api.github.com 401)을 나눈다.
// 끝내 실패한 서버는 잠시(연결 실패·서버 오류 1분, 막힘은 서버가 말한 만큼 1~10분) 바로 실패로 건너뛴다 — 인터넷이 끊겼을 때
// 요청마다 다시 시도하느라 한참 걸리지 않게. 연결 실패·서버 오류로 건너뛰던 것은 새 prepare 를 시작할 때 잊고
// (ForgetConnectionFailures — ConnectionEpoch 도 늘려 다른 곳의 실패 기억도 잊게), 오프라인 판정은 어느 서버든 응답이 오면 지운다.
// GitHub 에서 받는 작은 파일은 HttpCache 가 잠깐 기억한다.
internal static class Http
{
    private static readonly HttpClient Client = CreateClient(redirect: true);
    private static readonly HttpClient NoRedirectClient = CreateClient(redirect: false);
    private const int Attempts = 3;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    // 새 prepare 를 시작할 때마다(ForgetConnectionFailures) 1 늘어난다 — 다른 곳의 "조금 전 받지 못함" 기억도 같이 잊게 (GameVersions)
    private static int _connectionEpoch;
    public static int ConnectionEpoch => Volatile.Read(ref _connectionEpoch);

    // 안내 문구의 주어: GitHub 의 여러 주소는 "GitHub 이", 그 밖은 "서버(<주소>)가"
    public static string ServerSubject(string? host) =>
        host == null ? "서버가" : IsGitHub(host) ? "GitHub 이" : $"서버({host})가";

    private static bool IsGitHub(string host) =>
        Uri.TryCreate("https://" + host, UriKind.Absolute, out var uri) && ServerGroup(uri) == "GitHub";

    private static HttpClient CreateClient(bool redirect,
                                           Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        // 전체 시간 제한 대신 연결 제한만 두고, 본문은 "멈춘 채 응답 없음" 기준으로 끊는다(큰 파일도 받게)
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = redirect };
        if (connect != null) handler.ConnectCallback = connect;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ActPluginRepack");
        return client;
    }

    // 시험용: 연결을 대신 맺는다 (연결 거부를 바로 흉내 — Windows 의 루프백은 거부까지 SYN 재시도로 약 2초 걸린다).
    // 설정하면 그것을 쓰는 클라이언트를 따로 만들고, null 로 되돌리면 원래 클라이언트를 쓴다 (실제 실행 경로는 그대로).
    private static (HttpClient Redirect, HttpClient NoRedirect)? _testClients;
    private static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? _connectOverride;
    internal static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectOverride
    {
        get => _connectOverride;
        set
        {
            _connectOverride = value;
            _testClients = value == null ? null : (CreateClient(true, value), CreateClient(false, value));
        }
    }

    private static HttpClient ClientFor(bool redirect) =>
        _testClients is { } t ? (redirect ? t.Redirect : t.NoRedirect) : redirect ? Client : NoRedirectClient;

    // 시험용: 프로세스 전역 상태(건너뛰는 서버, 오프라인 판정)를 처음처럼 — 시험끼리 영향을 주지 않게
    internal static void ResetForTests()
    {
        lock (Down)
        {
            Down.Clear();
            Unreachable.Clear();
            _offlineUntil = default;
        }
        Interlocked.Increment(ref _connectionEpoch);
    }

    public static string? GetStringOrNull(string url) =>
        GetBytesOrNull(url) is { } bytes ? new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8).ReadToEnd() : null;

    public static byte[]? GetBytesOrNull(string url)
    {
        bool cacheable = HttpCache.Cacheable(url);
        var cached = cacheable ? HttpCache.Get(url) : null;
        if (cached is { Fresh: true }) return cached.Body;

        // 헤더 뒤 본문을 받다 끊기면(연결 끊김, 60초 무응답) 한 번 더
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = Send(url, etag: cached?.ETag);
                if (resp == null) return null;
                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && cached != null)
                {
                    HttpCache.Touch(url, cached);
                    return cached.Body;
                }
                using var body = OpenBody(resp);
                using var ms = new MemoryStream();
                body.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                // 데이터 파일 자리에 HTML 이 왔으면(호텔·PC방 로그인 페이지 같은 가로채기) 기억하지 않는다.
                // 원래 HTML 인 것은 릴리스 첨부 파일 목록(expanded_assets)뿐 — .atom / .json 자리의 HTML 은 기억하지 않는다
                bool interceptedPage = resp.Content.Headers.ContentType?.MediaType == "text/html" && !HttpCache.HtmlExpected(url);
                if (cacheable && !interceptedPage) HttpCache.Put(url, bytes, resp.Headers.ETag?.ToString());
                return bytes;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                if (attempt >= 2) throw BodyFailed(url, ex);
                Wait(TimeSpan.FromSeconds(3) * BackoffScale, url, $"받는 도중 끊김 ({ex.Message})");
            }
        }
    }

    // 응답 본문을 받다 끊김(연결 끊김, 60초 무응답 — IOException / HttpRequestException)을 연결 실패로 알린다.
    // 큰 파일을 OpenBody 로 직접 받는 쪽도 쓴다.
    public static NetworkException BodyFailed(string url, Exception ex) =>
        new($"{url} 에서 받는 도중 연결이 끊겼습니다. ({ex.Message})", url: url);

    // 큰 파일용: 응답 본문을 스트림으로 (60초 동안 데이터가 없으면 IOException). 호출자가 Dispose.
    public static Stream OpenBody(HttpResponseMessage resp) =>
        new IdleTimeoutStream(resp.Content.ReadAsStream(), TimeSpan.FromSeconds(60));

    // 응답 헤더까지 받는다. 404 면 null, etag 를 주고 바뀌지 않았으면 304 응답. 호출자가 Dispose.
    // api.github.com 이면 GitHub 토큰을 붙이고(token 을 주면 그것을) 응답의 사용량을 기억한다.
    // redirect: false 면 다른 주소로 넘어가지 않고 3xx 응답을 그대로 돌려준다 (Location 헤더를 읽는 쪽).
    public static HttpResponseMessage? Send(string url, string? token = null, string? etag = null, bool redirect = true)
    {
        bool api = url.StartsWith("https://api.github.com/", StringComparison.Ordinal);
        var uri = new Uri(url);
        string server = uri.Authority;
        if (Unavailable(server, url) is { } skip) throw skip;
        for (int attempt = 1; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (api && (token ?? GitHub.Token?.Token) is { } bearer)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            if (etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (api) GitHub.CountRequest(url);

            HttpResponseMessage resp;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Operation.Token))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30)); // 응답 헤더까지
                try
                {
                    resp = ClientFor(redirect).Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
                catch (OperationCanceledException) when (Operation.Token.IsCancellationRequested)
                {
                    throw; // 사용자가 취소
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    // 연결 단계 실패(끊김, 방화벽, 응답 없음)는 한 번만 다시 — 시간 제한을 여러 번 기다리면 몇 분이 걸린다
                    if (attempt >= 2)
                        throw Failed(new NetworkException($"{url} 에 연결하지 못했습니다. 인터넷 연결을 확인하세요. ({ex.Message})", url: url),
                                     Cause.Connection, server, connection: ServerGroup(uri));
                    Wait(Backoff(attempt), url, ex is OperationCanceledException ? "응답 없음" : $"연결 실패 ({ex.Message})");
                    continue;
                }
            }

            Reachable(); // 응답이 왔다 = 인터넷은 된다 (상태 코드와 무관)
            if (api) GitHub.Observe(resp);
            if (!redirect && (int)resp.StatusCode is >= 300 and < 400) return resp;
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { resp.Dispose(); return null; }
            if (resp.IsSuccessStatusCode || (etag != null && resp.StatusCode == System.Net.HttpStatusCode.NotModified)) return resp;

            int status = (int)resp.StatusCode;
            string host = req.RequestUri!.Host;
            TimeSpan? retryAfter = RetryAfter(resp);
            if (api && status == 401)
            {
                resp.Dispose();
                throw new NetworkException($"{url} 에서 받지 못했습니다. " + (token != null ? "GitHub 이 토큰을 인증하지 않았습니다 (틀렸거나 만료됨)."
                    : GitHub.TokenRejectedAdvice(GitHub.Token?.Source)), NetworkFailure.TokenRejected, url, status);
            }
            if (api && status is 403 or 429 &&
                resp.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0")
            {
                // 시간당 한도 소진 — 초기화(최대 1시간)까지 다시 해도 소용없다
                resp.Dispose();
                var limit = GitHub.Last;
                throw new GitHubLimitException(
                    $"GitHub API 요청 한도(시간당 {limit?.Limit ?? 60}회)를 다 썼습니다. 한도는 {limit?.Reset.ToLocalTime():HH:mm} 에 초기화됩니다. " +
                    (limit?.Authenticated == true ? "초기화된 뒤에 다시 실행하세요."
                        : "초기화된 뒤에 다시 실행하거나, GitHub 토큰을 등록해 한도를 시간당 5000회로 늘리세요."),
                    limit?.Reset, limit?.Authenticated == true);
            }

            bool throttled = status == 429 || (status == 403 && retryAfter != null);
            bool transient = throttled || status is 500 or 502 or 503 or 504;
            resp.Dispose();
            TimeSpan wait = retryAfter ?? Backoff(attempt);
            if (transient && attempt < Attempts && wait <= MaxWait)
            {
                Wait(retryAfter is { } r ? r * BackoffScale : wait, url, // (Backoff 는 이미 BackoffScale 을 곱함)
                     throttled ? $"서버가 잠시 막음 (HTTP {status})" : $"서버 오류 (HTTP {status})");
                continue;
            }
            if (throttled)
                throw Failed(new ThrottledException(
                    $"{url} 에서 받지 못했습니다. {host} 서버가 요청을 잠시 막았습니다 (HTTP {status}" +
                    (retryAfter is { } ra ? $", {Math.Ceiling(ra.TotalMinutes):0}분쯤 뒤에 풀림" : "") +
                    "). 같은 IP 에서 요청이 많을 때 생기는 일이니 몇 분 뒤에 다시 실행하세요.", host, retryAfter), Cause.Throttled, server);
            // 거절: 다른 4xx(403, 400, 407, 410, 451 ...)는 주소·권한·프록시 문제라 다시 해도 같다 — 건너뛰기에 넣지 않는다
            if (status is >= 400 and < 500 and not 408)
                throw new NetworkException($"{url} 에서 받지 못했습니다. 서버가 요청을 거절했습니다 (HTTP {status}). 다시 해도 같습니다 — " +
                                           "주소가 바뀌었거나, 접근 권한이 없거나, 프록시·방화벽이 막고 있을 수 있습니다.",
                                           NetworkFailure.Refused, url, status);
            // 서버가 오류로 응답 — 인터넷 연결 문제가 아니다. 서버 쪽 일시 오류(500/502/503/504)만 잠시 건너뛴다
            var error = new NetworkException($"{url} 에서 받지 못했습니다. 서버가 오류로 응답했습니다 (HTTP {status}).",
                                             NetworkFailure.ServerError, url, status);
            throw transient ? Failed(error, Cause.ServerError, server) : error;
        }
    }

    // 포기한 까닭 (서버 건너뛰기에 쓴다)
    private enum Cause { Connection, Throttled, ServerError }

    // 포기할 때만 부른다 (다시 시도해서 받았으면 문제 아님). 그 서버(server)를 잠시 건너뛴다 — 인터넷이
    // 끊긴 상태에서 요청마다 7초씩 다시 시도하며 한참 걸리지 않게, 막힌 서버를 계속 두드려 더 오래 막히지 않게.
    // 연결 실패(connection: 서버 묶음)가 서로 다른 묶음 두 곳에서 잇달아 나면(그 사이 어느 서버든 응답이 오면 처음부터
    // 다시 셈) 인터넷이 끊긴 것으로 보고 1분 동안 모든 요청을 바로 실패로 넘긴다.
    private static ToolException Failed(ToolException ex, Cause cause, string server, string? connection = null)
    {
        var until = DateTime.UtcNow + (ex is ThrottledException { RetryAfter: { } ra }
            ? TimeSpan.FromTicks(Math.Clamp(ra.Ticks, TimeSpan.FromMinutes(1).Ticks, TimeSpan.FromMinutes(10).Ticks))
            : TimeSpan.FromMinutes(1));
        lock (Down)
        {
            Down[server] = (until, cause);
            if (connection != null && Unreachable.Add(connection) && Unreachable.Count >= 2) _offlineUntil = DateTime.UtcNow.AddMinutes(1);
        }
        return ex;
    }

    // 서버별 "잠시 건너뜀" (연결 실패·서버 오류 1분, 막힘은 서버가 말한 만큼 1~10분)
    private static readonly Dictionary<string, (DateTime Until, Cause Cause)> Down = [];
    private static readonly HashSet<string> Unreachable = []; // 마지막 응답 이후 연결에 실패한 서버 묶음
    private static DateTime _offlineUntil;

    // 오프라인 판정에서 한 곳으로 셀 서버 묶음. GitHub 의 여러 주소(github.com, api.github.com, raw.githubusercontent.com,
    // objects.githubusercontent.com ...)는 같은 곳으로 본다 — GitHub 하나만 막힌 것(방화벽, DNS)을 인터넷 끊김으로 보지 않게.
    internal static string ServerGroup(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        return host is "github.com" or "githubusercontent.com" || host.EndsWith(".github.com", StringComparison.Ordinal) ||
               host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)
            ? "GitHub"
            : uri.Authority.ToLowerInvariant();
    }

    // 어느 서버에서든 응답이 왔다 — 인터넷은 되므로 연결 실패 묶음 수와 오프라인 판정을 지운다 (서버별 건너뜀은 그대로)
    private static void Reachable()
    {
        lock (Down)
        {
            if (Unreachable.Count == 0 && _offlineUntil == default) return;
            Unreachable.Clear();
            _offlineUntil = default;
        }
    }

    // 건너뛸 때도 처음 실패한 까닭(막힘 / 연결 실패 / 서버 오류)의 예외로 — 화면이 "아직 공개 전" 이 아니라 진짜 원인을 안내하게
    private static ToolException? Unavailable(string server, string url)
    {
        lock (Down)
        {
            if (DateTime.UtcNow < _offlineUntil)
                return new NetworkException($"인터넷에 연결되지 않은 것 같아 {url} 에서 받지 않고 건너뛰었습니다 (서버 {Unreachable.Count}곳 연결 실패). 인터넷 연결을 확인하세요.",
                                            url: url);
            if (!Down.TryGetValue(server, out var d)) return null;
            if (DateTime.UtcNow >= d.Until) { Down.Remove(server); return null; }
            var left = d.Until - DateTime.UtcNow;
            return d.Cause switch
            {
                Cause.Throttled => new ThrottledException($"{server} 서버가 조금 전 요청을 막아 {Math.Ceiling(left.TotalMinutes):0}분 동안 {url} 에서 받지 않고 건너뜁니다.", server, left),
                Cause.ServerError => new NetworkException($"조금 전 {server} 서버가 오류로 응답해 {url} 에서 받지 않고 잠시 건너뜁니다. 서버 쪽 문제일 수 있으니 잠시 후 다시 실행하세요.",
                                                          NetworkFailure.ServerError, url),
                _ => new NetworkException($"조금 전 {server} 서버에 연결하지 못해 {url} 에서 받지 않고 잠시 건너뜁니다. 인터넷 연결을 확인하세요.", url: url),
            };
        }
    }

    // 새 작업을 시작할 때: 연결 실패·서버 오류로 건너뛰던 서버는 다시 시도한다 (인터넷을 다시 연결했거나 서버가 돌아왔을 수 있으므로).
    // 막힌 서버는 서버가 말한 시간까지 그대로 — 다시 두드리면 더 오래 막힐 수 있다.
    public static void ForgetConnectionFailures()
    {
        lock (Down)
        {
            foreach (var key in Down.Where(kv => kv.Value.Cause != Cause.Throttled).Select(kv => kv.Key).ToList()) Down.Remove(key);
            Unreachable.Clear();
            _offlineUntil = default;
        }
        Interlocked.Increment(ref _connectionEpoch);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(attempt == 1 ? 2 : 5) * BackoffScale;

    // 시험용: 다시 시도 전 기다림을 줄인다 (연결 실패 시험이 수십 초씩 걸리지 않게). Retry-After 로 기다릴 때도 곱한다
    // (10초 넘게 기다리라는 응답을 바로 실패로 넘기는 판단은 서버가 말한 원래 시간으로)
    internal static double BackoffScale = 1;

    // Retry-After: 초 또는 날짜. 이미 지났으면 0.
    private static TimeSpan? RetryAfter(HttpResponseMessage resp) =>
        resp.Headers.RetryAfter is { } ra
            ? ra.Delta ?? (ra.Date is { } at ? (at - DateTimeOffset.UtcNow is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero) : null)
            : null;

    // 취소되면 바로 OperationCanceledException
    internal static void Wait(TimeSpan delay, string url, string why)
    {
        Log.Note($"[net] {why} - {delay.TotalSeconds:0}초 뒤 다시 시도: {url}");
        if (Operation.Token.WaitHandle.WaitOne(delay)) Operation.Token.ThrowIfCancellationRequested();
    }
}

// GitHub 의 최신 릴리스 (태그 + 첨부 파일). 플러그인 / SharpVision / deucalion 이 쓴다.
internal sealed record GitHubRelease(string Repo, string Tag, IReadOnlyList<(string Name, string Url)> Assets)
{
    // 릴리스가 없으면 null (API 404, 웹은 릴리스 목록으로 넘어갈 때만). 받지 못하거나 형식이 다르면 ToolException.
    // 토큰이 있으면 API 먼저, 없으면 웹 페이지 먼저 (GitHub.Fetch).
    public static GitHubRelease? Latest(string repo) =>
        GitHub.Fetch($"release:{repo}", $"{repo} 최신 릴리스", () => FromApi(repo, "latest"), () => FromWeb(repo));

    // 태그로 (이 도구의 자동 업데이트 — 최신 릴리스가 아니라 v 태그를 고르므로)
    public static GitHubRelease? ByTag(string repo, string tag) =>
        GitHub.Fetch($"release:{repo}:{tag}", $"{repo} 릴리스 {tag}",
                     () => FromApi(repo, $"tags/{Uri.EscapeDataString(tag)}"), () => FromWebTag(repo, tag));

    // github.com/<repo>/releases/latest 는 최신 태그 페이지로 넘어가고, 첨부 파일 목록은 expanded_assets/<태그> 에 있다.
    // 넘어가는 주소(Location)만 읽는다 — 태그 페이지 전체를 받지 않게 (IP 를 나눠 쓰면 웹 요청도 막히기 쉽다).
    // 릴리스가 없다고 확실한 경우(저장소 없음 404, 릴리스 목록 페이지로 넘어감)만 null, 예상과 다르면 ToolException
    // (GitHub.Fetch 가 API 로 다시 확인하게).
    private static GitHubRelease? FromWeb(string repo)
    {
        string url = $"https://github.com/{repo}/releases/latest";
        Uri? location;
        int status;
        using (var resp = Http.Send(url, redirect: false))
        {
            if (resp == null) return null; // 저장소 없음
            status = (int)resp.StatusCode;
            location = resp.Headers.Location is { } loc ? new Uri(new Uri(url), loc) : null;
        }
        if (location is { Host: "github.com" })
        {
            var m = System.Text.RegularExpressions.Regex.Match(location.AbsolutePath, "/releases/tag/(.+)$");
            if (m.Success) return FromWebTag(repo, Uri.UnescapeDataString(m.Groups[1].Value));
            if (location.AbsolutePath.TrimEnd('/').Equals($"/{repo}/releases", StringComparison.OrdinalIgnoreCase))
                return null; // 릴리스가 없으면 목록 페이지로 간다
        }
        throw new ToolException($"{url} 이(가) 최신 태그 페이지로 넘어가지 않았습니다 (HTTP {status}{(location != null ? $", {location}" : "")}).");
    }

    // 그 태그의 릴리스가 없을 때도 404 지만 주소 형식이 바뀌어도 404 라 구분할 수 없다 — ToolException 으로 넘겨 API 가 확인하게
    private static GitHubRelease FromWebTag(string repo, string tag)
    {
        string url = $"https://github.com/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
        string page = Http.GetStringOrNull(url) ?? throw new ToolException($"{tag} 릴리스의 첨부 파일 목록을 찾지 못했습니다 ({url}).");
        var assets = System.Text.RegularExpressions.Regex
            .Matches(page, $@"href=""(/{System.Text.RegularExpressions.Regex.Escape(repo)}/releases/download/[^""]+)""",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(x => System.Net.WebUtility.HtmlDecode(x.Groups[1].Value)).Distinct()
            .Select(href => (Uri.UnescapeDataString(href[(href.LastIndexOf('/') + 1)..]), "https://github.com" + href))
            .ToList();
        if (assets.Count == 0) throw new ToolException($"{tag} 릴리스의 첨부 파일 목록을 읽지 못했습니다 ({url}).");
        return new GitHubRelease(repo, tag, assets);
    }

    // which: "latest" 또는 "tags/<태그>"
    private static GitHubRelease? FromApi(string repo, string which)
    {
        string api = $"https://api.github.com/repos/{repo}/releases/{which}";
        string? json = Http.GetStringOrNull(api);
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var assets = root.GetProperty("assets").EnumerateArray()
                .Select(a => (a.GetProperty("name").GetString() ?? throw new ToolException($"GitHub 릴리스 정보에 첨부 파일 이름이 없습니다 ({api})."),
                              a.GetProperty("browser_download_url").GetString() ?? throw new ToolException($"GitHub 릴리스 정보에 첨부 파일 주소가 없습니다 ({api}).")))
                .ToList();
            return new GitHubRelease(repo, root.GetProperty("tag_name").GetString()
                                           ?? throw new ToolException($"GitHub 릴리스 정보에 태그가 없습니다 ({api})."), assets);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ToolException($"GitHub 릴리스 정보의 형식이 예상과 다릅니다 ({api}): {ex.Message}");
        }
    }

    public string? AssetUrl(Func<string, bool> match) => Assets.Where(a => match(a.Name)).Select(a => a.Url).FirstOrDefault();
}

// 읽기마다 시간 제한: 데이터가 일정 시간 안 오면 IOException (멈춘 다운로드가 영원히 걸리지 않게).
// 현재 작업이 취소되면 OperationCanceledException.
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idle) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Operation.Token);
        cts.CancelAfter(idle);
        try
        {
            return inner.ReadAsync(buffer.AsMemory(offset, count), cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!Operation.Token.IsCancellationRequested)
        {
            throw new IOException($"{idle.TotalSeconds:0}초 동안 데이터가 오지 않았습니다.");
        }
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
