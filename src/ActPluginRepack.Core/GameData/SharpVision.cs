using System.IO.Compression;

// config.json 에 "sharpVision" 이 없을 때 GitHub 최신 릴리스를 받아 tools\SharpVision\<태그>\ 에 둔다.
// 릴리스는 EXDSchema 갱신마다 patch 버전이 오르므로, 버전별 폴더를 써서 스키마가 바뀌면 새로 추출되게 하고
// (같은 폴더면 skip-existing 때문에 옛 스키마 CSV 가 남는다) 이전 버전 폴더는 지운다.
internal static class SharpVisionRelease
{
    private const string Repo = "pnsdh/SharpVision";

    // SharpVision.exe 경로. 받을 수 없고 캐시도 없으면 null.
    public static string? Resolve(Workspace ws)
    {
        string baseDir = ws.Tool("SharpVision");
        string? tag = null, url = null;
        try
        {
            if (GitHubRelease.Latest(Repo) is { } release)
            {
                tag = release.Tag;
                url = release.AssetUrl(n => n == $"SharpVision-{release.Tag}-win-x64.zip");
            }
        }
        catch (ToolException ex)
        {
            Log.Warn($"[csv] SharpVision 최신 릴리스 확인 실패: {ex.Message}");
        }

        if (tag != null && url != null && Install(baseDir, tag, url) is { } exe)
        {
            // 이전 버전 폴더 정리 — 다른 프로그램이 잡고 있어 못 지워도 상관없다 (다음에 다시)
            Files.DeleteOthers(baseDir, Path.GetDirectoryName(exe)!, "csv");
            return exe;
        }

        // 오프라인, 받기 실패 등: 받아 둔 버전이 있으면 그걸 쓴다
        string? cached = Directory.Exists(baseDir)
            ? Directory.GetDirectories(baseDir).Where(d => !d.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "SharpVision.exe")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (cached != null) Log.Warn($"[csv] 받아 둔 SharpVision 사용: {cached}");
        return cached;
    }

    // tools\SharpVision\<태그>\SharpVision.exe 를 준비한다. 받기/풀기/확인에 실패하면 경고하고 null (받아 둔 것을 쓰게).
    private static string? Install(string baseDir, string tag, string url)
    {
        string dir = Path.Combine(baseDir, tag);
        string exe = Path.Combine(dir, "SharpVision.exe");
        if (File.Exists(exe)) return exe;
        try
        {
            Log.Note($"[csv] SharpVision {tag} 받는 중: {url}");
            byte[] zip = Http.GetBytes(url);
            // 폴더가 있으면 exe 만 격리되고 남은 것 — 바꿔 넣는다
            Files.InstallDirectory(dir, tmp =>
            {
                ZipFile.ExtractToDirectory(new MemoryStream(zip), tmp);
                if (!File.Exists(Path.Combine(tmp, "SharpVision.exe")))
                    throw new ToolException($"{url} 안에 SharpVision.exe 가 없습니다.");
            });
            return exe;
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Log.Warn($"[csv] SharpVision {tag} 를 받지 못함 - {Errors.Describe(ex)}");
            return null;
        }
    }
}

// SharpVision(게임 데이터 -> CSV 추출 CLI) 연동. https://github.com/pnsdh/SharpVision
//
// SharpVision 은 실행 파일 옆 output/extract/<지역>/<게임버전>/csv/<언어>/<시트>.csv 에 추출하고,
// 이미 있는 파일은 건너뛴다(--overwrite 로 강제). 지역 이름은 Region.Name 과 같다.
internal static class SharpVision
{
    public static string CsvRoot(string exe, GameClient client) =>
        Path.Combine(Path.GetDirectoryName(exe)!, "output", "extract", client.Region!.Name, client.Version!, "csv");

    // 이 클라이언트의 모든 언어에 필요한 시트가 다 있으면 빈 목록.
    public static List<string> MissingSheets(string csvRoot, Region region) =>
        region.SharpVisionLangs
            .SelectMany(lang => CsvConverter.RequiredSheets.Select(s => Path.Combine(lang, s + ".csv")))
            .Where(rel => !File.Exists(Path.Combine(csvRoot, rel)))
            .ToList();

    // --sheets 지원(v1.1.0+)이면 필요한 시트만, 아니면 전체 추출.
    // 부분 추출하면 SharpVision 이 csv/<lang>/.partial 을 남겨 diff 가 나머지 시트를 삭제로 오인하지 않게 한다.
    public static void ExtractCsv(string exe, GameClient client, bool overwrite)
    {
        List<string> args = [client.SqpackDir, "csv", "--lang", string.Join(",", client.Region!.SharpVisionLangs)];
        if (SupportsSheets(exe)) args.AddRange(["--sheets", string.Join(",", CsvConverter.RequiredSheets)]);
        else Log.Note("    (이 SharpVision 은 --sheets 미지원 - 전체 시트 추출)");
        if (overwrite) args.Add("--overwrite");

        Log.Note($"    > {Path.GetFileName(exe)} {string.Join(" ", args.Select(Quote))}");
        Proc.Check(Run(exe, args, Proc.DefaultTimeout, line => Log.Note("      " + line)), "SharpVision");
    }

    // --help 는 sqpack 을 읽지 않고 바로 끝난다.
    private static bool SupportsSheets(string exe) =>
        Run(exe, ["--help"], TimeSpan.FromMinutes(1), null).Stdout.Contains("--sheets", StringComparison.Ordinal);

    // .dll 이면 dotnet 으로 실행 (빌드만 하고 publish 안 한 경우)
    private static (int Code, string Stdout, string Stderr) Run(string exe, List<string> args, TimeSpan timeout, Action<string>? onLine)
    {
        bool dll = exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        return Proc.Run(dll ? "dotnet" : exe, dll ? [exe, .. args] : args, Path.GetDirectoryName(exe)!, null, timeout, onLine);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
