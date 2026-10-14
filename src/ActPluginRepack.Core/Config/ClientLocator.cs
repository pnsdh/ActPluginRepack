using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// 설치된 게임 클라이언트 찾기 (GUI 첫 설정용 — 사용자가 경로를 직접 적지 않아도 되게).
//
// 후보 출처: 실행 중인 게임, 기본 설치 경로, 설치 기록(레지스트리), XIVLauncher 설정, Steam 라이브러리.
// 지역은 boot\ 의 런처로 가린다: 한국판은 FFXIV_Boot.exe(액토즈), 글로벌판은 ffxivboot.exe / ffxivlauncher64.exe.
// 그 밖(중국/대만)은 확실한 표시를 몰라 지역을 비워 두고 사용자가 고르게 한다.
internal sealed record DetectedClient(string InstallDir, Region? Region, string? Version, string Source);

internal static class ClientLocator
{
    public static List<DetectedClient> Detect()
    {
        var found = new List<(string Dir, string Source)>();
        void Add(string? dir, string source)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try { dir = Path.GetFullPath(dir.Trim().Trim('"')); }
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
        Add(XivLauncherGamePath(), "XIVLauncher 설정");
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

    // 설치 폴더의 boot\ 런처로 지역 판별 (모르면 null)
    public static Region? InferRegion(string installDir)
    {
        string boot = Path.Combine(installDir, "boot");
        if (File.Exists(Path.Combine(boot, "FFXIV_Boot.exe"))) return Region.Find("korean");
        if (File.Exists(Path.Combine(boot, "ffxivboot.exe")) || File.Exists(Path.Combine(boot, "ffxivlauncher64.exe")))
            return Region.Find("global");
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

    // 프로그램 추가/제거 기록 중 FINAL FANTASY XIV 의 설치 위치
    private static IEnumerable<string> UninstallLocations()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string[] keys =
        [
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (string key in keys)
        {
            using var root = hive.OpenSubKey(key);
            if (root == null) continue;
            foreach (string name in root.GetSubKeyNames())
            {
                using var app = root.OpenSubKey(name);
                if (app?.GetValue("DisplayName") is not string display || !display.Contains("FINAL FANTASY XIV", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (app.GetValue("InstallLocation") is string loc && loc.Length > 0) yield return loc;
            }
        }
    }

    private static string? XivLauncherGamePath()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "launcherConfigV3.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("GamePath", out var p) ? p.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return null; }
    }

    // Steam 라이브러리 폴더들 (steamapps\libraryfolders.vdf 의 "path")
    private static IEnumerable<string> SteamLibraries()
    {
        if (!OperatingSystem.IsWindows()) return [];
        string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam == null) return [];
        var libs = new List<string> { steam };
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                libs.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                    .Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        catch (IOException) { }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
