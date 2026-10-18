using System.Text.RegularExpressions;

namespace FFXIV.Signatures;

// 게임 exe 주변 정보
public static partial class GameExe
{
    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$")]
    private static partial Regex VersionPattern();

    // exe 옆 ffxivgame.ver 의 게임 버전 ("2026.10.06.0000.0000"). 파일이 없거나(따로 떼어 둔 exe 등) 읽지 못하거나
    // 경로가 잘못되었거나(빈 문자열 등) 형식이 다르면 null. exePath 가 null 이면 ArgumentNullException.
    public static string? ReadVersion(string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (dir == null) return null;
            string path = Path.Combine(dir, "ffxivgame.ver");
            if (!File.Exists(path)) return null;
            string version = File.ReadAllText(path).Trim();
            return VersionPattern().IsMatch(version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
