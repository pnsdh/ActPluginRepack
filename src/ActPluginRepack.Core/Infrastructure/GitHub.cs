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

    // 다른 쪽(API / 웹)으로 다시 해 볼 실패: 받지 못함, 형식이 다름
    private static bool Fallible(Exception ex) => ex is ToolException or System.Xml.XmlException || Errors.IsDataFormat(ex);

    private static void Blocked(bool api, Exception ex)
    {
        DateTime? until = ex switch
        {
            GitHubLimitException g => g.Reset?.UtcDateTime ?? DateTime.UtcNow.AddMinutes(10),
            // Http 의 서버 건너뛰기와 같은 규칙: 서버가 말한 시간을 1~10분으로 (말하지 않으면 1분)
            ThrottledException t => DateTime.UtcNow + Http.BackOff(t.RetryAfter),
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
        string json = Http.GetString(url);
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
        string xml = Http.GetString(url);
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

    // api.github.com 의 시간당 한도 (토큰 없음: IP 당, 토큰: 계정당)
    public const int AnonymousLimit = 60, TokenLimit = 5000;

    // 사용량 한 줄 (CLI github, 고급 화면, 설정 화면이 같은 말로)
    public static string Describe(GitHubRateLimit limit, string? source) =>
        $"시간당 {limit.Limit}회 중 {limit.Remaining}회 남음, {limit.Reset.ToLocalTime():HH:mm} 초기화 ({source ?? "토큰 없음"})";

    // 지금 사용량 한 줄(Ok), 또는 확인하지 못한 까닭 (CLI status, 고급 화면, 설정 화면이 같은 말로).
    // brief: 좁은 칸용 ("확인 불가 (까닭)"), 아니면 문장. 흔한 실패(네트워크, 형식이 다른 응답 등)만 실패 문장으로 — 그 밖의 예외(버그)는 그대로 올린다.
    public static (bool Ok, string Text) TryDescribeUsage(bool brief = false)
    {
        try
        {
            return Query() is { } limit ? (true, Describe(limit, Token?.Source)) : (false, UsageUnknown(null, brief));
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            return (false, UsageUnknown(ex, brief));
        }
    }

    // 사용량을 확인하지 못했을 때의 문장 (ex: 그 까닭, 모르면 null)
    public static string UsageUnknown(Exception? ex, bool brief = false) =>
        brief
            ? ex == null ? "확인 불가" : $"확인 불가 ({Errors.Describe(ex)})"
            : ex == null ? "사용량을 확인하지 못했습니다." : $"사용량을 확인하지 못했습니다: {Errors.Describe(ex)}";

    private static long? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) && long.TryParse(v.FirstOrDefault(), out long n) ? n : null;

    private static string? LoadSaved()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(TokenFile)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException || Errors.IsFileError(ex))
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

// GitHub 의 최신 릴리스 (태그 + 첨부 파일). 플러그인 / SharpVision / deucalion 이 쓴다.
internal sealed record GitHubRelease(string Repo, string Tag, IReadOnlyList<(string Name, string Url)> Assets)
{
    // 게시 시각 (UTC)과 시험판 여부 — List 만 채운다 (Latest / ByTag 는 null / false)
    public DateTime? PublishedAt { get; init; }
    public bool Prerelease { get; init; }

    // 최근 릴리스 count 개(최대 100), 최신부터. 초안(게시 전)은 뺀다. 받지 못하거나 형식이 다르면 ToolException.
    // 개발용(replay)이라 웹 페이지 대신 API 한 번으로 — 웹의 릴리스 목록은 쪽마다 10개에 게시 시각을 읽기 번거롭다.
    public static IReadOnlyList<GitHubRelease> List(string repo, int count = 100)
    {
        string api = $"https://api.github.com/repos/{repo}/releases?per_page={Math.Clamp(count, 1, 100)}";
        string json = Http.GetString(api);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Where(r => !(r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True))
                .Select(r => new GitHubRelease(repo, r.GetProperty("tag_name").GetString()
                                                     ?? throw new ToolException($"GitHub 릴리스 정보에 태그가 없습니다 ({api})."), ReadAssets(r, api))
                {
                    PublishedAt = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetDateTime().ToUniversalTime() : null,
                    Prerelease = r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ToolException($"GitHub 릴리스 목록의 형식이 예상과 다릅니다 ({api}): {ex.Message}");
        }
    }

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
            return new GitHubRelease(repo, root.GetProperty("tag_name").GetString()
                                           ?? throw new ToolException($"GitHub 릴리스 정보에 태그가 없습니다 ({api})."), ReadAssets(root, api));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ToolException($"GitHub 릴리스 정보의 형식이 예상과 다릅니다 ({api}): {ex.Message}");
        }
    }

    // API 의 릴리스 하나의 첨부 파일 (이름, 받을 주소)
    private static List<(string Name, string Url)> ReadAssets(JsonElement release, string api) =>
        release.GetProperty("assets").EnumerateArray()
            .Select(a => (a.GetProperty("name").GetString() ?? throw new ToolException($"GitHub 릴리스 정보에 첨부 파일 이름이 없습니다 ({api})."),
                          a.GetProperty("browser_download_url").GetString() ?? throw new ToolException($"GitHub 릴리스 정보에 첨부 파일 주소가 없습니다 ({api}).")))
            .ToList();

    public string? AssetUrl(Func<string, bool> match) => Assets.Where(a => match(a.Name)).Select(a => a.Url).FirstOrDefault();
}
