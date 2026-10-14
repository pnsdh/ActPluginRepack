// xiv-data-oxidizer(게임 데이터 -> CSV, Rust) 연동 — SharpVision 대신 쓸 수 있는 추출기.
// https://github.com/fae-industries/xiv-data-oxidizer (ironworks + EXDSchema, SaintCoinach rawexd 의 후속)
//
// 릴리스 바이너리가 없어 소스를 받아 빌드한다 (git + Rust/cargo 필요). config.json:
//   "oxidizer": true          tools\xiv-data-oxidizer\src 에 clone, 실행마다 소스·스키마(EXDSchema latest) 갱신 + 빌드
//   "oxidizer": "D:\\...\\xiv-data-oxidizer"   직접 관리하는 clone (스키마를 고쳐 쓸 때). 빌드만 하고 git 은 안 건드림.
//
// 인자는 게임 설치 폴더 하나뿐이고, 현재 폴더의 schemas\<시트>.yml 을 읽어 output\<언어>\<시트>.csv 에 **모든 시트**를
// 쓴다(글로벌 4개 언어 약 900MB, 30여 초). 그래서 임시 작업 폴더에서 돌리고 필요한 시트만 캐시로 옮긴 뒤 지운다.
// 결과는 SharpVision 과 바이트 단위로 같다(7.57 글로벌 4개 언어 + 한국어, 7개 시트 확인).
internal static class Oxidizer
{
    private const string Repo = "https://github.com/fae-industries/xiv-data-oxidizer";

    // 실행 파일 경로와 스키마 폴더. 쓸 수 없으면 ToolException.
    public sealed record Tool(string Exe, string Schemas, string SchemaRev);

    public static Tool Prepare(Workspace ws, string setting)
    {
        bool managed = setting.Equals("true", StringComparison.OrdinalIgnoreCase);
        string src = managed ? ws.Tool("xiv-data-oxidizer", "src") : setting;
        string exe = Path.Combine(src, "target", "release", "xiv-data-oxidizer.exe");

        if (managed)
        {
            try
            {
                if (!Directory.Exists(Path.Combine(src, ".git")))
                {
                    Log.Note($"[csv] xiv-data-oxidizer 받는 중: {Repo}");
                    Directory.CreateDirectory(Path.GetDirectoryName(src)!);
                    Proc.Check(Proc.Run("git", ["clone", "-q", "--recurse-submodules", Repo, src], ws.Root), "git clone");
                }
                else Proc.Check(Proc.Run("git", ["pull", "-q", "--ff-only"], src), "git pull");
                // README 대로 스키마는 서브모듈 고정 커밋이 아니라 EXDSchema 의 최신(latest 브랜치)을 쓴다
                Proc.Check(Proc.Run("git", ["submodule", "update", "-q", "--init", "--remote"], src), "git submodule update");
            }
            catch (Exception ex) when (ex is ToolException or System.ComponentModel.Win32Exception)
            {
                if (!File.Exists(exe)) throw new ToolException($"xiv-data-oxidizer 를 받지 못했습니다 (git 필요): {ex.Message}");
                Log.Warn($"[csv] xiv-data-oxidizer 갱신 실패 - 받아 둔 것 사용 ({ex.Message})");
            }
        }
        else if (!File.Exists(Path.Combine(src, "Cargo.toml")))
            throw new ToolException($"xiv-data-oxidizer 소스 폴더가 아닙니다 (Cargo.toml 없음): {src}");

        try
        {
            // 바뀐 게 없으면 1초 안에 끝난다
            Proc.Check(Proc.Run("cargo", ["build", "--release", "--quiet"], src), "cargo build");
        }
        catch (Exception ex) when (ex is ToolException or System.ComponentModel.Win32Exception)
        {
            if (!File.Exists(exe))
                throw new ToolException($"xiv-data-oxidizer 를 빌드하지 못했습니다 (Rust 필요, https://rustup.rs): {ex.Message}");
            Log.Warn($"[csv] xiv-data-oxidizer 빌드 실패 - 이전 빌드 사용 ({ex.Message})");
        }

        string schemas = Path.Combine(src, "schemas");
        if (!Directory.Exists(schemas)) throw new ToolException($"EXDSchema 가 없습니다: {schemas}. git submodule update --init 으로 받으세요.");
        return new Tool(exe, schemas, SchemaKey(schemas));
    }

    // 캐시 키: 스키마 커밋 + 커밋 안 된 수정이 있으면 그 내용 해시 (직접 관리하는 clone 에서 스키마를 고쳐 쓰는 경우)
    private static string SchemaKey(string schemas)
    {
        string rev = "local";
        try
        {
            var r = Proc.Run("git", ["rev-parse", "--short", "HEAD"], schemas, null, TimeSpan.FromMinutes(1));
            if (r.Code == 0) rev = r.Stdout.Trim();
            var dirty = Proc.Run("git", ["status", "--porcelain", "."], schemas, null, TimeSpan.FromMinutes(1));
            if (dirty.Code == 0 && dirty.Stdout.Trim().Length == 0) return rev;
        }
        catch (Exception ex) when (ex is ToolException or System.ComponentModel.Win32Exception) { } // git 없음
        // 수정됐거나 git 이 없으면 yml 내용으로
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (string f in Directory.GetFiles(schemas, "*.yml").Order(StringComparer.Ordinal))
        {
            byte[] b = File.ReadAllBytes(f);
            sha.TransformBlock(b, 0, b.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return $"{rev}-{Convert.ToHexString(sha.Hash!)[..8].ToLowerInvariant()}";
    }

    // 이 클라이언트의 필요한 시트를 캐시에 추출하고 캐시 폴더(<캐시>\<leaf>\<시트>.csv)를 돌려준다.
    // 캐시는 게임 버전 + 스키마 커밋별 — 같으면 재사용 (--overwrite 로 강제 재추출).
    public static string ExtractCsv(Workspace ws, Tool tool, GameClient client, bool overwrite)
    {
        var region = client.Region!;
        string cache = ws.Tool("xiv-data-oxidizer", "cache", region.Name, $"{client.Version}-{tool.SchemaRev}");
        if (!overwrite && Missing(cache, region).Count == 0)
        {
            Log.Note($"[csv] 기존 추출본 사용: {cache}");
            return cache;
        }

        string work = ws.Tool("xiv-data-oxidizer", "work");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        try
        {
            CopyDirectory(tool.Schemas, Path.Combine(work, "schemas"));
            string install = Path.GetDirectoryName(client.GameDir)!; // ...\game 의 위 = 설치 폴더
            Log.Note($"[csv] xiv-data-oxidizer 로 추출 (스키마 {tool.SchemaRev}): {install}");
            var r = Proc.Run(tool.Exe, [install], work);
            Proc.Check(r, "xiv-data-oxidizer");
            // 스키마와 열 개수가 안 맞는 시트는 "Failed to export <시트>. ..." 로 건너뛰고 계속한다
            var failed = r.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => CsvConverter.RequiredSheets.Any(s => l.StartsWith($"Failed to export {s}.", StringComparison.Ordinal)))
                .ToList();
            if (failed.Count > 0) throw new ToolException($"xiv-data-oxidizer 가 일부 시트를 추출하지 못했습니다: {string.Join(" / ", failed)}");

            string tmp = cache + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            foreach (string leaf in region.CsvLeafs)
            {
                string from = Path.Combine(work, "output", LangCode(leaf));
                Directory.CreateDirectory(Path.Combine(tmp, leaf));
                foreach (string sheet in CsvConverter.RequiredSheets)
                {
                    string f = Path.Combine(from, sheet + ".csv");
                    if (!File.Exists(f)) throw new ToolException($"xiv-data-oxidizer 결과에 {LangCode(leaf)}/{sheet}.csv 파일이 없습니다.");
                    // 스키마가 없는 시트는 머리줄 없이 나온다 — 변환기가 열 이름을 못 찾으므로 여기서 거른다
                    using (var reader = new StreamReader(f))
                        if (reader.ReadLine() is not { } header || !header.StartsWith("#,", StringComparison.Ordinal))
                            throw new ToolException($"xiv-data-oxidizer 결과 {LangCode(leaf)}/{sheet}.csv 에 열 이름이 없습니다 (스키마 없음).");
                    File.Copy(f, Path.Combine(tmp, leaf, sheet + ".csv"));
                }
            }
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            Directory.Move(tmp, cache);
            // 같은 지역의 이전 캐시는 지운다
            foreach (string old in Directory.GetDirectories(Path.GetDirectoryName(cache)!)
                         .Where(d => !d.Equals(cache, StringComparison.OrdinalIgnoreCase)))
                Directory.Delete(old, recursive: true);
            return cache;
        }
        finally
        {
            // 전체 시트 추출본(수백 MB)을 지운다. 백신 검사 중이라 못 지우면 알리고 다음 실행 때 다시 지운다
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"[csv] 작업 폴더를 지우지 못함 (다음 실행 때 다시 지움): {work}");
            }
        }
    }

    private static List<string> Missing(string cache, Region region) =>
        region.CsvLeafs.SelectMany(leaf => CsvConverter.RequiredSheets.Select(s => Path.Combine(cache, leaf, s + ".csv")))
            .Where(f => !File.Exists(f)).ToList();

    // input\csv 의 폴더명 -> oxidizer 의 언어 코드
    private static string LangCode(string leaf) => Region.DataLang(leaf);

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from).Where(f => Path.GetFileName(f) != ".git")) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from).Where(d => Path.GetFileName(d) != ".git"))
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
