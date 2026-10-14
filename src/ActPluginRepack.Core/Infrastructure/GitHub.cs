using System.Security.Cryptography;
using System.Text.Json;

// GitHub API 토큰과 사용량.
//
// api.github.com 은 토큰 없이 IP 당 시간당 60회(PC방·공유기처럼 IP 를 나눠 쓰면 남의 사용량까지 합쳐짐), 토큰이
// 있으면 계정당 5000회. 그래서 토큰이 없으면 아래 Fetch 로 웹 페이지를 먼저 써 보통 API 를 한 번도 쓰지 않는다
// (7.57 기준 prepare 한 번: 예전 4회 -> 0회). raw.githubusercontent.com 과 릴리스 파일 다운로드는 한도와 무관하다.
//
// 토큰 출처 (앞에서부터):
//   1) 환경 변수 GITHUB_TOKEN
//   2) 이 PC 에 저장한 토큰 — %LOCALAPPDATA%\ActPluginRepack\github-token.bin (DPAPI: 이 Windows 사용자만 풂).
//      작업 폴더와 상관없이 GUI / CLI 가 같이 쓴다.
//   3) GitHub CLI 로그인 (gh auth token) — gh 를 쓰는 사람은 따로 설정할 필요가 없다
// 토큰은 공개 저장소를 읽기만 하므로 권한(scope)이 없는 토큰이면 충분하다.
//
// API 를 아끼는 조회 (Fetch): 최신 릴리스와 최근 커밋은 같은 정보를 웹 페이지(releases/latest 이동, expanded_assets,
// commits/<브랜치>.atom)에서도 얻을 수 있고, 이건 API 한도와 무관하다. 그래서 토큰이 없으면 웹 먼저, 있으면 형식이
// 약속된 API 먼저 쓰고, 앞의 것이 실패하면(형식 변경, 한도 초과 등) 뒤의 것으로. 그래서 웹 쪽은 형식이 예상과 다르면
// null(없음)이 아니라 예외를 던져야 한다 — "없음"은 확실할 때만. 결과(null 포함)는 HttpCache.FreshFor(3분) 동안 기억한다 —
// GUI 에서 연달아 눌러도 다시 묻지 않게.
internal sealed record GitHubRateLimit(int Limit, int Remaining, int Used, DateTimeOffset Reset)
{
    public bool Authenticated => Limit > 60;
}

// 커밋 하나: Message 는 첫 줄, Date 는 UTC
internal sealed record GitHubCommit(string Sha, string Message, DateTime Date);

internal static class GitHub
{
    public const string TokenPageUrl = "https://github.com/settings/personal-access-tokens/new";

    private static readonly string TokenFile = Path.Combine(AppPaths.UserRoot, "github-token.bin");
    private static readonly byte[] Entropy = "ActPluginRepack.GitHubToken"u8.ToArray();

    private static readonly Lock Gate = new();
    private static (string Token, string Source)? _token;
    private static bool _resolved;
    private static GitHubRateLimit? _last;

    // 이 프로세스가 보낸 api.github.com 요청 수 (/rate_limit 제외 — 한도를 쓰지 않음). prepare 가 끝에 알려 준다.
    private static int _requests;
    public static int Requests => Volatile.Read(ref _requests);
    public static void CountRequest(string url)
    {
        if (!url.StartsWith("https://api.github.com/rate_limit", StringComparison.Ordinal)) Interlocked.Increment(ref _requests);
    }

    // 마지막 api.github.com 응답의 한도 (이 프로세스에서 아직 요청하지 않았으면 null)
    public static GitHubRateLimit? Last { get { lock (Gate) return _last; } }

    // 시험용: 토큰 찾기를 대신한다 (PC 에 GITHUB_TOKEN / 저장한 토큰 / gh 로그인이 있는지와 상관없이 같은 결과가 나오게).
    // 돌려준 값이 그대로 Token 이 된다 (null = 토큰 없음).
    internal static Func<(string Token, string Source)?>? TokenOverride;

    // 시험용: 이 프로세스에서 기억한 것(조회 결과, 막힌 쪽, 사용량, 찾은 토큰)을 처음처럼
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Cache.Clear();
            _webBlockedUntil = _apiBlockedUntil = default;
            _resolved = false;
            _token = null;
            _last = null;
        }
    }

    // 쓸 토큰과 출처 설명. 없으면 null.
    public static (string Token, string Source)? Token
    {
        get
        {
            if (TokenOverride is { } over) return over();
            lock (Gate)
            {
                if (_resolved) return _token;
                _token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } env ? (env.Trim(), EnvSource)
                    : LoadSaved() is { } saved ? (saved, SavedSource)
                    : GhCliToken() is { } gh ? (gh, GhSource)
                    : null;
                _resolved = true;
                return _token;
            }
        }
    }

    // 토큰 출처 (Token 의 Source)
    public const string EnvSource = "환경 변수 GITHUB_TOKEN", SavedSource = "저장한 토큰", GhSource = "GitHub CLI 로그인";

    // GitHub 이 쓰는 토큰을 거절했을 때 고칠 방법 (출처마다 — 환경 변수는 저장한 토큰보다 먼저 쓰여 저장한 것을 지워도 소용없다).
    // registerAt: 토큰을 등록하는 곳 안내 (GUI 는 "고급 모드의 설정 화면에서 ")
    public static string TokenRejectedAdvice(string? source, string registerAt = "") => source switch
    {
        EnvSource => "환경 변수 GITHUB_TOKEN 에 든 GitHub 토큰이 틀렸거나 만료되었습니다. 환경 변수 GITHUB_TOKEN 을 고치거나 지우세요 " +
                     "(이 환경 변수가 등록한 토큰보다 먼저 쓰입니다).",
        GhSource => "GitHub CLI(gh) 로그인 토큰이 틀렸거나 만료되었습니다. gh auth login 으로 다시 로그인하거나, " +
                    $"{registerAt}토큰을 따로 등록하세요.",
        _ => $"등록된 GitHub 토큰이 틀렸거나 만료되었습니다. {registerAt}토큰을 지우거나 다시 등록하세요.",
    };

    public static bool HasSavedToken => File.Exists(TokenFile);

    // 토큰을 확인하고(GitHub 에 물어봄) 이 PC 에 저장한다. 틀린 토큰이면 ToolException.
    public static GitHubRateLimit SaveToken(string token)
    {
        token = token.Trim().Trim('﻿').Trim(); // 파이프로 넘기면 BOM 이 붙기도 한다
        if (token.Length == 0 || !token.All(c => c is > ' ' and < '\x7F')) throw new ToolException("토큰 형식이 올바르지 않습니다.");
        var limit = Query(token) ?? throw new ToolException("GitHub 에서 사용량 정보를 받지 못했습니다.");
        if (!limit.Authenticated) throw new ToolException("GitHub 이 토큰을 인증하지 않았습니다. 토큰이 맞는지, 만료되지 않았는지 확인하세요.");
        if (!OperatingSystem.IsWindows()) throw new ToolException("토큰 저장은 Windows 에서만 지원합니다.");
        byte[] data = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        Files.WriteAtomic(TokenFile, data);
        Forget();
        return limit;
    }

    public static void DeleteToken()
    {
        File.Delete(TokenFile);
        Forget();
    }

    private static void Forget()
    {
        lock (Gate) { _resolved = false; _token = null; _last = null; }
    }

    // 지금 사용량 (GET /rate_limit — 이 요청은 한도를 쓰지 않는다). token 을 주면 그 토큰으로, 아니면 쓰는 토큰으로.
    public static GitHubRateLimit? Query(string? token = null)
    {
        const string url = "https://api.github.com/rate_limit";
        using var resp = Http.Send(url, token);
        if (resp == null) return null;
        using var body = Http.OpenBody(resp);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var core = doc.RootElement.GetProperty("resources").GetProperty("core");
            var limit = new GitHubRateLimit(core.GetProperty("limit").GetInt32(), core.GetProperty("remaining").GetInt32(),
                core.GetProperty("used").GetInt32(), DateTimeOffset.FromUnixTimeSeconds(core.GetProperty("reset").GetInt64()));
            if (token != null) return limit;
            lock (Gate)
            {
                // /rate_limit 은 몇 초 늦게 갱신되기도 한다 — 같은 주기에 이 프로세스가 받은 응답 헤더가 더 최신이면 그것
                if (_last is { } seen && seen.Reset == limit.Reset && seen.Remaining < limit.Remaining) limit = seen;
                return _last = limit;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ToolException($"GitHub 사용량 정보의 형식이 예상과 다릅니다: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw Http.BodyFailed(url, ex); // 본문을 받다 끊김
        }
    }

    // api.github.com 응답 헤더의 한도를 기억한다 (Http 가 부름). /rate_limit 은 Query 가 본문으로 처리.
    public static void Observe(HttpResponseMessage resp)
    {
        if (resp.RequestMessage?.RequestUri?.AbsolutePath == "/rate_limit") return;
        if (Header(resp, "X-RateLimit-Limit") is { } limit && Header(resp, "X-RateLimit-Remaining") is { } remaining &&
            Header(resp, "X-RateLimit-Reset") is { } reset)
        {
            // 검색 등 다른 종류의 한도는 따로 세므로 core 만
            if (resp.Headers.TryGetValues("X-RateLimit-Resource", out var res) && res.FirstOrDefault() is { } r && r != "core") return;
            lock (Gate) _last = new GitHubRateLimit((int)limit, (int)remaining, (int)(limit - remaining), DateTimeOffset.FromUnixTimeSeconds(reset));
        }
    }

    // ---- API / 웹 페이지 조회 ----
    private static readonly Dictionary<string, (DateTime At, object? Value)> Cache = [];
    private static readonly TimeSpan CacheFor = HttpCache.FreshFor;

    // 막힌 쪽 기억: 웹이 막히면(429) 한동안 API 먼저, API 한도가 다 차면 초기화까지 웹만 (이 프로세스 동안)
    private static DateTime _webBlockedUntil, _apiBlockedUntil;

    public static T? Fetch<T>(string key, string what, Func<T?> api, Func<T?> web) where T : class
    {
        lock (Gate)
            if (!HttpCache.Bypass && Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return (T?)hit.Value;
        bool webBlocked, apiBlocked;
        lock (Gate) (webBlocked, apiBlocked) = (DateTime.UtcNow < _webBlockedUntil, DateTime.UtcNow < _apiBlockedUntil);
        bool apiFirst = webBlocked || (Token != null && !apiBlocked);
        T? value;
        try
        {
            value = apiFirst ? api() : web();
        }
        catch (Exception ex) when (Fallible(ex))
        {
            Blocked(apiFirst, ex);
            bool otherBlocked = apiFirst ? webBlocked : apiBlocked;
            if (otherBlocked) throw AsTool(ex, what); // 다른 쪽도 막혀 있으면 기다려도 소용없다 — 이 오류 그대로
            Log.Note($"[github] {what}: {(apiFirst ? "API" : "웹 페이지")}에서 실패 ({ex.Message}) - {(apiFirst ? "웹 페이지" : "API")}로 다시");
            try
            {
                value = apiFirst ? web() : api();
            }
            catch (Exception ex2) when (Fallible(ex2))
            {
                Blocked(!apiFirst, ex2);
                throw AsTool(ex2, what);
            }
        }
        lock (Gate) Cache[key] = (DateTime.UtcNow, value);
        return value;
    }

    // 호출하는 쪽은 ToolException 만 받으면 된다 (형식이 바뀐 응답의 JsonException / XmlException 등도 감싸서)
    private static Exception AsTool(Exception ex, string what) =>
        ex as ToolException ?? new ToolException($"{what}: 받은 자료의 형식이 예상과 다릅니다. 원본 페이지의 형식이 바뀌었을 수 있습니다. ({ex.Message})");

    private static bool Fallible(Exception ex) =>
        ex is ToolException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException
            or System.Xml.XmlException;

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    private static void Blocked(bool api, Exception ex)
    {
        DateTime? until = ex switch
        {
            GitHubLimitException g => g.Reset?.UtcDateTime ?? DateTime.UtcNow.AddMinutes(10),
            // Http 의 서버 건너뛰기와 같은 규칙: 서버가 말한 시간을 1~10분으로 (말하지 않으면 1분)
            ThrottledException t => DateTime.UtcNow + Clamp(t.RetryAfter ?? TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            _ => null,
        };
        if (until == null) return;
        lock (Gate)
        {
            if (api) _apiBlockedUntil = until.Value;
            else _webBlockedUntil = until.Value;
        }
    }

    // 브랜치(와 경로)의 최근 커밋, 최신부터. 없으면 빈 목록.
    public static IReadOnlyList<GitHubCommit> RecentCommits(string repo, string branch, string? path, int count) =>
        Fetch($"commits:{repo}:{branch}:{path}:{count}", $"{repo} 커밋",
              () => CommitsFromApi(repo, branch, path, count), () => CommitsFromWeb(repo, branch, path, count))!;

    private static List<GitHubCommit> CommitsFromApi(string repo, string branch, string? path, int count)
    {
        string url = $"https://api.github.com/repos/{repo}/commits?sha={branch}&per_page={count}" + (path != null ? $"&path={path}" : "");
        string json = Http.GetStringOrNull(url) ?? throw new ToolException($"서버에서 찾지 못했습니다: {url}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(c => new GitHubCommit(
            c.GetProperty("sha").GetString() ?? throw new ToolException($"커밋 목록에 커밋 번호가 없습니다 ({url})."),
            (c.GetProperty("commit").GetProperty("message").GetString() ?? "").Split('\n')[0].Trim(),
            c.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTime().ToUniversalTime())).ToList();
    }

    // github.com/<repo>/commits/<branch>[/<path>].atom — 최근 20개, 제목은 메시지 첫 줄, updated 는 커밋 시각
    private static List<GitHubCommit> CommitsFromWeb(string repo, string branch, string? path, int count)
    {
        string url = $"https://github.com/{repo}/commits/{branch}{(path != null ? "/" + path : "")}.atom";
        string xml = Http.GetStringOrNull(url) ?? throw new ToolException($"서버에서 찾지 못했습니다: {url}");
        System.Xml.Linq.XNamespace ns = "http://www.w3.org/2005/Atom";
        var list = System.Xml.Linq.XDocument.Parse(xml).Root!.Elements(ns + "entry").Select(e => new GitHubCommit(
            System.Text.RegularExpressions.Regex.Match((string?)e.Element(ns + "id") ?? "", "Commit/([0-9a-f]{40})").Groups[1].Value,
            ((string?)e.Element(ns + "title") ?? "").Trim().Split('\n')[0].Trim(), // 제목 앞뒤에 줄바꿈이 있다
            DateTimeOffset.Parse((string?)e.Element(ns + "updated") ?? "", System.Globalization.CultureInfo.InvariantCulture).UtcDateTime))
            .Take(count).ToList();
        // 비어 있거나 형식이 다르면 API 로 넘긴다 (경로에 커밋이 없는 경우도 API 가 확인)
        if (list.Count == 0 || list.Any(c => c.Sha.Length == 0)) throw new ToolException($"커밋 목록을 읽지 못했습니다 ({url}).");
        return list;
    }

    // 사람이 읽는 한 줄: "52/60 남음, 15:42 초기화 (토큰 없음)"
    public static string Describe(GitHubRateLimit limit, string? source) =>
        $"{limit.Remaining}/{limit.Limit} 남음, {limit.Reset.ToLocalTime():HH:mm} 초기화 ({source ?? "토큰 없음"})";

    private static long? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) && long.TryParse(v.FirstOrDefault(), out long n) ? n : null;

    private static string? LoadSaved()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(TokenFile)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null; // 다른 사용자/PC 에서 만든 파일 등 — 없는 것으로
        }
    }

    // gh 가 설치돼 있고 로그인돼 있으면 그 토큰
    private static string? GhCliToken()
    {
        try
        {
            var (code, stdout, _) = Proc.Run("gh", ["auth", "token", "--hostname", "github.com"], Path.GetTempPath(), null,
                                             TimeSpan.FromSeconds(10));
            return code == 0 && stdout.Trim() is { Length: > 0 } t && !t.Any(char.IsWhiteSpace) ? t : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ToolException or IOException)
        {
            return null; // gh 없음 / 시간 초과
        }
    }
}
