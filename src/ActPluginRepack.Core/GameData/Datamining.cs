using System.Text.Json;

// 공개 데이터마이닝 저장소에서 CSV 받기 (SharpVision 이 없을 때, 그리고 클라이언트가 없는 cn/tc).
//
//   en/de/fr/ja  xivapi/ffxiv-datamining         csv/<lang>/  xiv-data-oxidizer 형식 (SharpVision 과 바이트 동일)
//   ko           Ra-Workspace/ffxiv-datamining-ko csv/        같은 형식, 커밋 메시지에 게임 버전
//   cn / tc      thewakingsands/ffxiv-datamining-* (루트)      SaintCoinach 형식, 커밋 메시지에 게임 버전
//   (모든 언어)   fae-industries/xiv-data           exd/<lang>/  xiv-data-oxidizer 형식, 커밋 메시지에 패치 번호
//                 ("Patch 7.56", "TC Patch 7.25", "Add patch 7.56 Simplified Chinese chs data"). 패치 당일 수동 갱신이라
//                 위 저장소들보다 늦을 수 있어 뒤 순위 (--sources 이름 xivdata).
//
// 버전 판별: 그 경로의 최근 커밋 중 버전 정보가 있는 첫 메시지를 본다("Update with latest schemas" 같은 건 건너뜀).
// 게임 버전 문자열이 있으면 정확히 일치로 본다. 패치 번호뿐이면(xivapi "7.57 (#120)", "Patch 7.45 hotfix" = 7.45h)
// GameVersions 대응표로 게임 버전과 맞춘다. 그것도 안 되면 그 커밋(버전 정보가 있는 커밋, 없으면 최신 커밋)의 날짜가
// 게임 빌드 날짜 이후인지로 추정한다 —
// 빌드 날짜는 출시보다 앞서므로 이전 패치 데이터는 보통 그보다 먼저 커밋돼 있다.
internal static class Datamining
{
    // Name: --sources 이름 (datamining / xivdata)
    public sealed record Source(string Name, string Repo, string Branch, string Prefix);

    public static IEnumerable<Source> For(string leaf)
    {
        yield return leaf switch
        {
            "en" or "de" or "fr" or "ja" => new("datamining", "xivapi/ffxiv-datamining", "master", $"csv/{leaf}/"),
            "ko" => new("datamining", "Ra-Workspace/ffxiv-datamining-ko", "refactor", "csv/"),
            "cn" => new("datamining", "thewakingsands/ffxiv-datamining-cn", "master", ""),
            "tc" => new("datamining", "thewakingsands/ffxiv-datamining-tc", "main", ""),
            _ => throw new ToolException($"'{leaf}' 언어의 datamining 출처가 없습니다."),
        };
        yield return new("xivdata", "fae-industries/xiv-data", "main", $"exd/{Region.DataLang(leaf)}/");
    }

    // Sha: 확인한 시점에 그 경로를 마지막으로 바꾼 커밋 — Download 는 브랜치가 아니라 이 커밋에서 받는다
    // (확인과 받기 사이에 새 커밋이 올라오거나 raw CDN 이 파일마다 다른 때의 것을 주어 옛/새 CSV 가 섞이지 않게)
    public sealed record Status(string Description, bool Usable, string? Sha = null);

    // Check 로 확인한 커밋 (Download 에 sha 를 넘기지 않았을 때 쓴다)
    private static readonly Dictionary<Source, string> Checked = [];

    // gameVersion 이 null 이면(클라이언트 없음) 최신본을 그대로 쓴다.
    public static Status Check(Source src, string? gameVersion)
    {
        var commits = GitHub.RecentCommits(src.Repo, src.Branch, src.Prefix.Length > 0 ? src.Prefix.TrimEnd('/') : null, 10);
        if (commits.Count == 0) return new Status($"{src.Repo} {src.Prefix} - 없음", false);
        string sha = commits[0].Sha;
        lock (Checked) Checked[src] = sha;
        var (message, date) = commits.Select(c => (c.Message, c.Date)).FirstOrDefault(c => HasVersion(c.Message) || Retail(c.Message) != null);
        if (message == null) (message, date) = (commits[0].Message, commits[0].Date);
        string what = $"{src.Repo} \"{message}\" ({date:yyyy-MM-dd})";
        Status Result(string verdict, bool usable) => new($"{what} - {verdict}", usable, sha);

        if (gameVersion == null) return Result("최신본 사용", true);
        if (message.Contains(gameVersion, StringComparison.Ordinal)) return Result("버전 일치", true);
        if (HasVersion(message)) return Result("다른 버전", false);

        // 패치 번호("7.57 (#120)", "Patch 7.56")는 대응표로 게임 버전과 맞춘다
        if (Retail(message) is { } retail && GameVersions.VersionOf(retail) is { } mapped)
            return mapped == gameVersion
                ? Result($"{retail} = {gameVersion}, 버전 일치", true)
                : Result($"{retail} = {mapped}, 다른 버전", false);

        var build = DateTime.ParseExact(gameVersion[..10], "yyyy.MM.dd", null, System.Globalization.DateTimeStyles.AssumeUniversal)
            .ToUniversalTime();
        return date >= build
            ? Result($"커밋이 빌드 날짜({gameVersion[..10]}) 이후라 이번 패치로 추정", true)
            : Result($"빌드 날짜({gameVersion[..10]}) 이전 커밋, 아직 미갱신", false);
    }

    private static bool HasVersion(string message) =>
        System.Text.RegularExpressions.Regex.IsMatch(message, @"\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}");

    // 메시지 앞의 패치 번호("7.57 (#120)", "v7.57") 또는 "Patch 7.56" / "Patch 7.45 hotfix"(-> 7.45h)
    internal static string? Retail(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"^v?(\d+\.\d+\w*)");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(message, @"\b[Pp]atch v?(\d+\.\d+\w*)( hotfix)?");
        return m.Success ? m.Groups[1].Value + (m.Groups[2].Success ? "h" : "") : null;
    }

    // 전부 받은 뒤에만 바꿔 넣는다 — 중간에 실패하면 두 버전이 섞인 폴더가 남지 않게.
    // sha: 확인한 커밋 (Status.Sha). 주지 않으면 이 출처를 마지막으로 Check 한 커밋, 그것도 없으면 브랜치 최신.
    public static void Download(Source src, IEnumerable<string> sheets, string dstDir, string? sha = null)
    {
        if (sha == null) lock (Checked) sha = Checked.GetValueOrDefault(src);
        string rev = sha ?? src.Branch;
        var files = new List<(string Name, byte[] Bytes)>();
        foreach (string sheet in sheets)
        {
            string url = $"https://raw.githubusercontent.com/{src.Repo}/{rev}/{src.Prefix}{sheet}.csv";
            files.Add((sheet + ".csv", Http.GetBytesOrNull(url) ?? throw new ToolException($"서버에서 찾지 못했습니다: {url}")));
        }
        foreach (var (name, bytes) in files) Files.WriteAtomic(Path.Combine(dstDir, name), bytes);
    }
}
