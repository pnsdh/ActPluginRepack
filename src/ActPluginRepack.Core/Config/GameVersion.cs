using System.Text.RegularExpressions;

// 게임 버전 문자열 ("2026.10.06.0000.0000", ffxivgame.ver). 폴더 이름(snapshots\<지역>\<버전>)과 순서 비교에
// 쓰이므로 형식이 맞는 것만 받는다.
internal static partial class GameVersion
{
    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? version) => version != null && Pattern().IsMatch(version);

    // 게임 폴더(ffxiv_dx11.exe 가 있는 곳)의 ffxivgame.ver. 없거나 형식이 다르면 null.
    public static string? ReadFrom(string gameDir)
    {
        string path = Path.Combine(gameDir, "ffxivgame.ver");
        if (!File.Exists(path)) return null;
        string version = File.ReadAllText(path).Trim();
        return IsValid(version) ? version : null;
    }
}
