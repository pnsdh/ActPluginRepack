using System.Xml.Linq;

// ACT 에서 플러그인 위치 찾기.
//
// ACT 설정(%APPDATA%\Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml)의 플러그인 목록에서 찾는다.
// 해루봇도 이 방식으로 등록한다(Plugins\FFXIV_ACT_Plugin_KR 등 언어별 폴더 — KR / Global 은 같은 공식 플러그인이라 함께 설치하고,
// _CN 은 중국판에 맞춰 고친 다른 플러그인이라 건드리지 않는다). 설치·되돌리기는 Install.

// Others: 함께 설치할 다른 언어 폴더 (해루봇의 KR / Global 중 ACT 설정에 적히지 않은 쪽)
internal sealed record ActTarget(string PluginPath, bool Enabled)
{
    public string PluginDir => Path.GetDirectoryName(PluginPath)!;
    public string? InstalledVersion => MaterialState.PluginVersionOf(PluginPath);
    public IReadOnlyList<string> Others { get; init; } = [];
    public IReadOnlyList<string> PluginDirs => [PluginDir, .. Others];
    public IReadOnlyList<string> PluginPaths => [.. PluginDirs.Select(d => Path.Combine(d, PluginRelease.PluginFile))];
}

internal static class ActLocator
{
    // 환경 변수 ACTREPACK_ACT_CONFIG 로 다른 ACT 설정 파일을 쓸 수 있다 (시험용 — 진짜 ACT 를 건드리지 않고 GUI 설치까지 돌려 보기)
    public static string ActConfigPath =>
        Environment.GetEnvironmentVariable("ACTREPACK_ACT_CONFIG") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "Config",
                       "Advanced Combat Tracker.config.xml");

    // ACT 설정에 등록된 플러그인 중 파일 이름이 fileName 인 것 (켜진 것 먼저). ACT 가 없으면 빈 목록.
    private static List<ActTarget> FindPlugins(string fileName)
    {
        if (!File.Exists(ActConfigPath)) return [];
        XDocument doc;
        try { doc = XDocument.Load(ActConfigPath); }
        catch (System.Xml.XmlException ex) { throw new ActTargetException(ActProblem.ConfigUnreadable, $"ACT 설정 파일을 읽지 못했습니다 ({ActConfigPath}): {ex.Message}"); }
        return doc.Descendants("ActPlugins").Elements("Plugin")
            .Select(p => (Path: (string?)p.Attribute("Path"), Enabled: string.Equals((string?)p.Attribute("Enabled"), "True", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.Path != null && Path.GetFileName(p.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ActTarget(p.Path!, p.Enabled))
            .OrderByDescending(t => t.Enabled)
            .ToList();
    }

    // FFXIV 플러그인 위치: 직접 준 폴더들(그대로), 아니면 ACT 설정에서 켜진 것 (해루봇이면 다른 언어 폴더도)
    public static ActTarget ResolveTarget(IReadOnlyList<string>? pluginDirs)
    {
        if (pluginDirs is { Count: > 0 })
        {
            var dirs = new List<string>();
            foreach (string d in pluginDirs)
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d));
                // 관리자 권한 재실행도 이 경로로 온다 — FFXIV 플러그인이 실제로 있는 폴더만 (엉뚱한 폴더에 쓰지 않게)
                if (!File.Exists(Path.Combine(full, PluginRelease.PluginFile))) throw new ToolException($"FFXIV_ACT_Plugin.dll 이 있는 폴더가 아닙니다: {d}");
                if (HaerubotDirName(full) == HaerubotCnDir) throw new ToolException($"{CnUnsupported} ({full})");
                if (!dirs.Any(x => Files.SamePath(x, full))) dirs.Add(full);
            }
            return new ActTarget(Path.Combine(dirs[0], PluginRelease.PluginFile), Enabled: true) { Others = dirs.Skip(1).ToList() };
        }
        var targets = FindPlugins(PluginRelease.PluginFile);
        if (targets.Count == 0)
            throw new ActTargetException(ActProblem.NotRegistered, $"ACT 에 등록된 FFXIV_ACT_Plugin 을 찾지 못했습니다 ({ActConfigPath}). ACT 나 해루봇에서 플러그인을 한 번 설치한 뒤 다시 하거나, 플러그인 폴더를 직접 지정하세요.");
        var enabled = targets.Where(t => t.Enabled).ToList();
        if (enabled.Count > 1)
            throw new ActTargetException(ActProblem.MultipleEnabled, $"켜진 FFXIV_ACT_Plugin 이 여러 개입니다. 설치할 폴더를 직접 지정하세요: {string.Join(", ", enabled.Select(t => t.PluginDir))}",
                                         enabled.Select(t => t.PluginDir).ToList());
        var target = enabled.FirstOrDefault() ?? targets[0];
        if (HaerubotDirName(target.PluginDir) is { } name)
        {
            // 해루봇: 공식 플러그인이 든 다른 언어 폴더도 함께 (ACT 설정이 중국 서버용을 가리키면 공식 플러그인 폴더만)
            string plugins = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.PluginDir)))!;
            var langs = HaerubotDirs.Select(n => Path.Combine(plugins, n))
                .Where(d => !Files.SamePath(d, target.PluginDir) && File.Exists(Path.Combine(d, PluginRelease.PluginFile))).ToList();
            if (name == HaerubotCnDir)
            {
                if (langs.Count == 0) throw new ActTargetException(ActProblem.ChineseOnly, $"{CnUnsupported} ({target.PluginDir})");
                return new ActTarget(Path.Combine(langs[0], PluginRelease.PluginFile), target.Enabled) { Others = langs.Skip(1).ToList() };
            }
            target = target with { Others = langs };
        }
        if (!File.Exists(target.PluginPath))
            throw new ActTargetException(ActProblem.FileMissing, $"ACT 설정에 적힌 플러그인 파일이 없습니다: {target.PluginPath}");
        return target;
    }

    private static readonly string[] HaerubotDirs = ["FFXIV_ACT_Plugin_KR", "FFXIV_ACT_Plugin_Global"];

    // 해루봇 언어 폴더의 화면 이름 (해루봇 폴더가 아니면 null)
    public static string? HaerubotLabel(string dir) => HaerubotDirName(dir) switch
    {
        "FFXIV_ACT_Plugin_KR" => "한국",
        "FFXIV_ACT_Plugin_Global" => "글로벌",
        _ => null,
    };
    private const string HaerubotCnDir = "FFXIV_ACT_Plugin_CN";
    private const string CnUnsupported = "해루봇의 중국 서버용 FFXIV 플러그인(FFXIV_ACT_Plugin_CN)은 공식 플러그인을 고친 다른 플러그인이라 설치하지 않습니다. " +
                                         "한국·글로벌 서버용 FFXIV 플러그인을 해루봇에서 설치한 뒤 다시 하세요.";

    // ACT\Plugins 아래 해루봇의 언어별 폴더면 그 이름 (아니면 null)
    private static string? HaerubotDirName(string dir)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (!Path.GetFileName(Path.GetDirectoryName(full) ?? "").Equals("Plugins", StringComparison.OrdinalIgnoreCase)) return null;
        string name = Path.GetFileName(full);
        return HaerubotDirs.Append(HaerubotCnDir).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // OverlayPlugin 의 resources 폴더 (켜진 OverlayPlugin.dll 옆). 없으면 null.
    public static string? ResolveOverlayResources()
    {
        var overlay = FindPlugins("OverlayPlugin.dll").FirstOrDefault(t => t.Enabled && File.Exists(t.PluginPath));
        string? dir = overlay != null ? Path.Combine(overlay.PluginDir, "resources") : null;
        return dir != null && Directory.Exists(dir) ? dir : null;
    }

    // OverlayPlugin.dll 옆의 resources 폴더인지 (관리자 권한 재실행으로 받은 경로도 확인)
    internal static bool IsOverlayResources(string dir) =>
        Path.GetFileName(dir).Equals("resources", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? "", "OverlayPlugin.dll"));
}
