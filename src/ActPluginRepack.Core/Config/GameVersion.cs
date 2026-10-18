using System.Text.RegularExpressions;

// 게임 버전 문자열 ("2026.10.06.0000.0000", ffxivgame.ver). 폴더 이름(snapshots\<지역>\<버전>)과 순서 비교에
// 쓰이므로 형식이 맞는 것만 받는다.
internal static partial class GameVersion
{
    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? version) => version != null && Pattern().IsMatch(version);

    // 앞의 날짜("2026.10.06" — exe 의 빌드 날짜와 같음)와, 마지막 자리를 뺀 것("2026.10.06.0000" — 같은 출시의 미리 배포·한국 전용
    // 중간 패치는 마지막 자리만 다르다). 짧으면 null
    public static string? DatePart(string version) => version.Length >= 10 ? version[..10] : null;
    public static string? BuildPart(string version) => version.Length >= 15 ? version[..15] : null;

    // 게임 폴더(ffxiv_dx11.exe 가 있는 곳)의 ffxivgame.ver. 없거나 형식이 다르면 null.
    public static string? ReadFrom(string gameDir)
    {
        string path = Path.Combine(gameDir, "ffxivgame.ver");
        if (!File.Exists(path)) return null;
        string version;
        try { version = File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (Errors.IsFileError(ex)) { return null; } // 런처가 업데이트하며 잡고 있음 등
        return IsValid(version) ? version : null;
    }
}
