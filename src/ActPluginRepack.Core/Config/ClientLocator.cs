using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// 설치된 게임 클라이언트 찾기 (GUI 첫 설정용 — 사용자가 경로를 직접 적지 않아도 되게).
//
// 후보 출처: 실행 중인 게임, 기본 설치 경로, 설치 기록(레지스트리), XIVLauncher(CN) 설정, Steam 라이브러리.
// 지역은 설치 폴더의 표시로 가린다: 중국판은 sdo\sdologin(SDO 로그인 모듈 — 런처는 글로벌판과 같은 ffxivboot.exe 라 먼저 본다),
// 한국판은 boot\FFXIV_Boot.exe(액토즈), 글로벌판은 boot\ffxivboot.exe / ffxivlauncher64.exe. 대만판은 확실한 표시를 몰라
// 지역을 비워 두고 사용자가 고르게 한다.
internal sealed record DetectedClient(string InstallDir, Region? Region, string? Version, string Source);

internal static class ClientLocator
{
    public static List<DetectedClient> Detect()
    {
        var found = new List<(string Dir, string Source)>();
        void Add(string? dir, string source)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            // 끝의 \ 는 뗀다 (설치 기록의 "...\KOREA\" 와 기본 경로가 같은 폴더로 묶이게)
            try { dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Trim().Trim('"'))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            // game\ 이나 ffxiv_dx11.exe 를 가리켜도 설치 폴더로
            if (File.Exists(dir)) dir = Path.GetDirectoryName(dir)!;
            if (Path.GetFileName(dir).Equals("game", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
            if (File.Exists(Path.Combine(dir, "game", "ffxiv_dx11.exe"))) found.Add((dir, source));
        }

        foreach (string exe in RunningGames()) Add(exe, "실행 중인 게임");

        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string baseDir in new[] { pf86, pf }.Where(d => d.Length > 0).Distinct())
        {
            Add(Path.Combine(baseDir, "FINAL FANTASY XIV - KOREA"), "기본 설치 경로");
            Add(Path.Combine(baseDir, "SquareEnix", "FINAL FANTASY XIV - A Realm Reborn"), "기본 설치 경로");
        }

        foreach (string dir in UninstallLocations()) Add(dir, "설치 기록");
        Add(XivLauncherGamePath("XIVLauncher"), "XIVLauncher 설정");
        Add(XivLauncherGamePath("XIVLauncherCN"), "XIVLauncherCN 설정");
        foreach (string lib in SteamLibraries()) Add(Path.Combine(lib, "steamapps", "common", "FINAL FANTASY XIV Online"), "Steam");

        return found
            .GroupBy(f => f.Dir, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                string dir = g.First().Dir;
                return new DetectedClient(dir, InferRegion(dir), GameVersion.ReadFrom(Path.Combine(dir, "game")),
                                          string.Join(", ", g.Select(x => x.Source).Distinct()));
            })
            .ToList();
    }

    // 기본으로 고르는(GUI 체크, detect --save) 지역: 이 도구가 설치하는 해루봇의 한국·글로벌 서버용 플러그인의 지역.
    // 중국·대만판은 찾아서 보여 주되 사용자가 고를 때만 쓴다 — 해루봇의 중국판은 별도 플러그인(TundraWork)이라 이 도구의 결과를
    // 쓰지 않고, 클라이언트가 늘면 시그니처를 그 exe 에도 맞춰야 하고 CSV 추출 등 시간도 든다.
    public static bool SelectedByDefault(Region? region) => region == Region.Global || region == Region.Korean;

    // 설치 폴더로 추정한 지역이 적어 둔 지역과 확실히 다른지 (경고·화면 표시용). 글로벌로 추정된 것은 런처만 보고 정한 것이라
    // 중국·대만판으로 적혀 있으면 다르다고 하지 않는다 (sdo 표시가 없는 중국판, 표시를 모르는 대만판)
    public static bool Contradicts(Region inferred, Region configured) =>
        inferred != configured && !(inferred == Region.Global && (configured == Region.SimplifiedChinese || configured == Region.TraditionalChinese));

    // 설치 폴더의 표시로 지역 판별 (모르면 null)
    public static Region? InferRegion(string installDir)
    {
        if (Directory.Exists(Path.Combine(installDir, "sdo", "sdologin"))) return Region.SimplifiedChinese;
        string boot = Path.Combine(installDir, "boot");
        if (File.Exists(Path.Combine(boot, "FFXIV_Boot.exe"))) return Region.Korean;
        if (File.Exists(Path.Combine(boot, "ffxivboot.exe")) || File.Exists(Path.Combine(boot, "ffxivlauncher64.exe")))
            return Region.Global;
        return null;
    }

    private static IEnumerable<string> RunningGames()
    {
        var paths = new List<string>();
        foreach (var p in Process.GetProcessesByName("ffxiv_dx11"))
        {
            try { if (p.MainModule?.FileName is { } f) paths.Add(f); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } // 권한 부족
            finally { p.Dispose(); }
        }
        return paths;
    }

    // 프로그램 추가/제거 기록 중 FINAL FANTASY XIV 의 설치 위치 (중국판은 "最终幻想14", 설치 위치가 없으면 아이콘 경로
    // ...\boot\ffxivboot.exe 에서)
    private static IEnumerable<string> UninstallLocations()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string[] keys =
        [
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        // 권한이 막힌 키(보안 프로그램 등)는 건너뛴다 — 하나 때문에 찾기 전체가 실패하지 않게
        var found = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (string key in keys)
        {
            try
            {
                using var root = hive.OpenSubKey(key);
                if (root == null) continue;
                foreach (string name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var app = root.OpenSubKey(name);
                        if (app?.GetValue("DisplayName") is not string display ||
                            !(display.Contains("FINAL FANTASY XIV", StringComparison.OrdinalIgnoreCase) || display.Contains("最终幻想", StringComparison.Ordinal)))
                            continue;
                        if (app.GetValue("InstallLocation") is string loc && loc.Length > 0) found.Add(loc);
                        else if (app.GetValue("DisplayIcon") is string icon && InstallDirOfIcon(icon) is { } dir) found.Add(dir);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException || Errors.IsFileError(ex)) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || Errors.IsFileError(ex)) { }
        }
        foreach (string loc in found) yield return loc;
    }

    // "...\boot\ffxivboot.exe,0" -> 설치 폴더 (boot 의 위). boot 안이 아니면 그 경로 그대로 (Add 가 확인한다)
    internal static string? InstallDirOfIcon(string icon)
    {
        string path = icon.Split(',')[0].Trim().Trim('"');
        try
        {
            string? boot = Path.GetDirectoryName(path);
            return boot != null && Path.GetFileName(boot).Equals("boot", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(boot)
                : path.Length > 0 ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return null; }
    }

    // XIVLauncher(글로벌) / XIVLauncherCN(중국판) 의 게임 경로 설정
    private static string? XivLauncherGamePath(string folder)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folder, "launcherConfigV3.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("GamePath", out var p) ? p.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException || Errors.IsFileError(ex)) { return null; }
    }

    // Steam 라이브러리 폴더들 (steamapps\libraryfolders.vdf 의 "path")
    private static IEnumerable<string> SteamLibraries()
    {
        if (!OperatingSystem.IsWindows()) return [];
        string? steam;
        try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
        catch (Exception ex) when (ex is System.Security.SecurityException || Errors.IsFileError(ex)) { return []; }
        if (steam == null) return [];
        var libs = new List<string> { steam };
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                libs.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                    .Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
