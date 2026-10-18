using System.Reflection;

// 이 프로그램의 버전과 새 버전 확인. 버전은 저장소 루트의 Directory.Build.props 한 곳에서 정한다.
//
// 이 저장소의 릴리스는 이 도구만 올리지만(패치된 플러그인은 pnsdh/FFXIV_ACT_Plugin_HotFix), 예전처럼 다른 태그가 섞여도
// 오알림이 없게 "최신 릴리스"가 아니라 릴리스 목록에서 이 도구의 태그(v1.2.3)만 골라 가장 높은 것과 비교한다. 목록은 토큰이 없으면 웹(releases.atom)으로 보므로 API 한도를
// 쓰지 않는다 (GitHub.Fetch). 확인에 실패하면 조용히 넘어간다 (알림은 있으면 좋은 것).
internal static class AppInfo
{
    public const string Repo = "pnsdh/ActPluginRepack";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string DiscordUrl = "https://discord.gg/R75tq68bA9"; // 문의·문제 보고

    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0);

    // 사람이 보는 버전: 2.0.0 (빌드 번호 0 은 뺌)
    public static string DisplayVersion =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Version.ToString(3);

    public sealed record Update(string Tag, Version Version, string Url);

    // 새 버전이 있으면 그 정보, 없거나 확인하지 못하면 null
    public static Update? CheckForUpdate()
    {
        try
        {
            // 시험용: ACTREPACK_UPDATE_FEED 폴더의 zip 이름(ActPluginRepack-<버전>-win-x64.zip)을 릴리스로 본다
            var tags = AppUpdate.Feed is { } feed
                ? Directory.GetFiles(feed, "ActPluginRepack-*-win-x64.zip")
                    .Select(f => "v" + Path.GetFileName(f)["ActPluginRepack-".Length..^"-win-x64.zip".Length]).ToList()
                : GitHub.Fetch("app-releases", "이 도구의 릴리스 목록", TagsFromApi, TagsFromWeb) ?? [];
            var best = tags.Select(t => (Tag: t, Version: ParseTag(t))).Where(x => x.Version != null)
                .OrderByDescending(x => x.Version).FirstOrDefault();
            return best.Version != null && IsNewer(best.Version, Version)
                ? new Update(best.Tag, best.Version, $"{RepoUrl}/releases/tag/{Uri.EscapeDataString(best.Tag)}")
                : null;
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            return null;
        }
    }

    // 이 도구의 태그만: "v1.2" / "v1.2.3" (v 필수, 접미사 없음). 패치본 태그(7.57, 7.56h1)와 시험판(v1.2.3-beta)은 null.
    public static Version? ParseTag(string tag) =>
        System.Text.RegularExpressions.Regex.Match(tag.Trim(), @"^[vV](\d+)\.(\d+)(?:\.(\d+))?$") is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0)
            : null;

    private static List<string> TagsFromApi()
    {
        string url = $"https://api.github.com/repos/{Repo}/releases?per_page=30";
        string json = Http.GetString(url);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .Select(r => r.GetProperty("tag_name").GetString() ?? throw new ToolException($"릴리스 목록에 태그가 없는 항목이 있습니다 ({url}).")).ToList();
    }

    // github.com/<repo>/releases.atom — 최근 릴리스들, 링크 끝이 태그. 릴리스가 하나도 없으면 빈 목록,
    // 피드가 아니거나 항목은 있는데 태그를 하나도 읽지 못하면 ToolException (GitHub.Fetch 가 API 로 다시 확인).
    //
    // 이 피드에는 시험판(prerelease) 표시가 없다 (API 는 prerelease 로 거른다). 그래서 시험판은 반드시 접미사가 붙은
    // 태그(v1.3.0-beta1 등)로 올린다는 규칙에 기댄다 — ParseTag 가 접미사 태그를 버리므로 웹으로 봐도 알림이 가지 않는다.
    // 릴리스마다 페이지를 더 받아 확인하는 것은 요청만 늘어 하지 않는다.
    private static List<string> TagsFromWeb()
    {
        string url = $"{RepoUrl}/releases.atom";
        string xml = Http.GetString(url);
        System.Xml.Linq.XNamespace ns = "http://www.w3.org/2005/Atom";
        var root = System.Xml.Linq.XDocument.Parse(xml).Root;
        if (root == null || root.Name != ns + "feed") throw new ToolException($"릴리스 목록 피드의 형식이 예상과 다릅니다 ({url}).");
        var entries = root.Elements(ns + "entry").ToList();
        var tags = entries
            .Select(e => (string?)e.Element(ns + "link")?.Attribute("href"))
            .Select(href => href != null && href.Contains("/releases/tag/") ? Uri.UnescapeDataString(href[(href.LastIndexOf('/') + 1)..]) : null)
            .Where(t => t != null).Select(t => t!).ToList();
        if (entries.Count > 0 && tags.Count == 0) throw new ToolException($"릴리스 목록 피드에서 태그를 읽지 못했습니다 ({url}).");
        return tags;
    }

    public static bool IsNewer(Version candidate, Version current) => Versions.IsNewer(candidate, current);
}
