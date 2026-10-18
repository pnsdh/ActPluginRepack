using System.Diagnostics;

// IDA 로 opcode 를 직접 추출한다 (Yarukon 이 아직 공개하지 않았을 때 / --ida).
//
// 작업 폴더(snapshots\<분석 대상 지역>\<버전>\ida\)에 Yarukon/FFXIVNetworkOpcodes 의 최신 finder 스크립트와
// 시그니처를 받고, 우리 래퍼(run_finder.py)와 보정(signatures.override.json)을 같이 둔 뒤 exe 복사본을 idat
// 헤드리스로 분석한다. 래퍼와 보정은 소스의 Core\Opcodes\ida\ 에 있고, 빌드 때 실행 파일 옆 ida\
// (AppContext.BaseDirectory\ida)로 복사된 것을 쓴다. 결과는 finder 가 쓰는 output\CN_<날짜>\opcodes.json (전체 목록).
// 분석 DB(~700MB)는 래퍼가 지우고, 결과 opcodes.json 은 작업 폴더에 캐시한다.
internal static class Ida
{
    private const string YarukonRaw = "https://raw.githubusercontent.com/Yarukon/FFXIVNetworkOpcodes/master";
    private static readonly string[] YarukonFiles = ["ffxiv_opcode_finder.py", "signatures.json", "machina.template"];
    // 우리 래퍼가 있는 ida\ 폴더: 실행 파일 옆 (GUI, 개발 빌드), 아니면 한 단계 위 (배포본의 cli\ActPluginRepack.exe)
    private static string OurDir()
    {
        string here = Path.Combine(AppContext.BaseDirectory, "ida");
        string up = Path.Combine(AppContext.BaseDirectory, "..", "ida");
        return OurFiles.All(f => File.Exists(Path.Combine(here, f))) || !OurFiles.All(f => File.Exists(Path.Combine(up, f)))
            ? here : Path.GetFullPath(up);
    }

    private static readonly string[] OurFiles = ["run_finder.py", "signatures.override.json"];
    private const string CacheName = "opcodes.json";

    // 설치 폴더 또는 실행 파일 경로 -> idat 실행 파일. IDA 9 는 idat.exe, 7/8 은 idat64.exe.
    public static string ResolveIdat(string path)
    {
        if (File.Exists(path)) return path;
        foreach (string name in new[] { "idat.exe", "idat64.exe" })
        {
            string p = Path.Combine(path, name);
            if (File.Exists(p)) return p;
        }
        throw new ToolException($"{path} 에 idat.exe / idat64.exe 가 없습니다.");
    }

    // 설정에 "ida" 가 없을 때: Program Files 의 IDA* 폴더 중 idat 가 있는 가장 높은 버전.
    // 폴더 이름에 버전이 있으면 9 이상만 (7.7 은 최신 finder 분석 도중 충돌).
    public static string? Detect()
    {
        try
        {
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(Environment.GetFolderPath).Where(p => p.Length > 0 && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(p => Directory.GetDirectories(p, "IDA*"))
                .Where(d => File.Exists(Path.Combine(d, "idat.exe")) || File.Exists(Path.Combine(d, "idat64.exe")))
                .Select(d => (Dir: d, Version: System.Text.RegularExpressions.Regex.Match(Path.GetFileName(d), @"\d+(\.\d+)+|\d+") is
                    { Success: true } m && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out var v) ? v : null))
                .Where(x => x.Version == null || x.Version.Major >= 9)
                .OrderByDescending(x => x.Version).ThenBy(x => x.Dir, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Dir).FirstOrDefault();
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            return null;
        }
    }

    public static string WorkDir(Workspace ws, Region region, string version) =>
        Path.Combine(ws.SnapshotDir(region, version), "ida");

    // exePath 를 region 의 exe 로 보고 분석한다 (region 이 중국판이 아니면 시그니처 보정을 덧씌움).
    // 캐시가 있으면(overwrite 아니면) 재사용.
    public static OpcodeSet FindOpcodes(string idat, string workDir, string exePath, Region region, bool overwrite)
    {
        string cache = Path.Combine(workDir, CacheName);
        string source = $"IDA {region.Name}";
        string ours = OurDir();
        // finder 는 저장소 최신본을 쓴다 — 패치 날 고쳐지면 다시 돌리게 finder·래퍼의 해시를 비교한다 (받지 못하면 받아 둔 결과).
        // 해시를 남기기 전에 만든 결과는 분석이 몇 분 걸리므로 그때 것과 같다고 보고 지금 해시를 남긴다 (trustUnstamped).
        // 새로 돌리는 것은 옆 폴더에서 — 끝까지 성공해야 받아 둔 결과를 바꾼다
        return ScriptCache.Run(workDir, cache, overwrite, trustUnstamped: true,
            fetch: Fetch,
            hash: finder => Files.Sha256Hex(
                finder.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => System.Text.Encoding.UTF8.GetBytes(kv.Value))
                    .Concat(OurFiles.SelectMany(f => File.ReadAllBytes(Path.Combine(ours, f)))).ToArray()),
            readCache: () => new OpcodeSet(source, OpcodeSources.ParseIpcJson(File.ReadAllText(cache), cache, aliases: null)),
            fresh: (finder, hash) => Analyze(idat, workDir, exePath, region, finder, ours, hash, source),
            new ScriptCache.Notes(
                FetchFailed: why => $"[opcode] Yarukon finder 를 받지 못해 ({why}) 받아 둔 IDA 결과 사용: {cache}",
                Cached: $@"[opcode] IDA 결과 캐시 사용: {cache}",
                Changed: "[opcode] Yarukon finder 가 바뀌어 IDA 로 다시 분석",
                RunFailed: why => $"[opcode] 바뀐 finder 로 IDA 를 다시 돌리지 못해 받아 둔 결과를 씀 - {why}"));
    }

    private static OpcodeSet Analyze(string idat, string dest, string exePath, Region region, Dictionary<string, string> finder,
                                     string ours, string hash, string source)
    {
        string workDir = ScriptCache.NewDir(dest);
        foreach (var (f, text) in finder)
        {
            // finder 는 래퍼가 UTF-8 로 읽고, signatures.json 은 래퍼가 ASCII 로 다시 쓴다
            File.WriteAllText(Path.Combine(workDir, f), text, new System.Text.UTF8Encoding(false));
            // 저장소 최신본을 그대로 실행하므로 어느 것을 실행했는지 남긴다 (문제가 생겼을 때 추적용)
            Log.Note($"[opcode] {f} (Yarukon master) sha256 {Files.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text))[..16]}");
        }
        foreach (string f in OurFiles) File.Copy(Path.Combine(ours, f), Path.Combine(workDir, f));
        string exe = Path.Combine(workDir, "ffxiv_dx11.exe");
        File.Copy(exePath, exe);

        // 경로 따옴표 문제를 피하려고 작업 폴더를 cwd 로 두고 상대 경로로 넘긴다.
        // 창 없이: idat 는 텍스트 화면 프로그램이라 그냥 띄우면 GUI 에서 검은 콘솔 창이 몇 분 동안 떠 있다.
        // TVHEADLESS=1 은 IDA 의 텍스트 화면 그리기를 끈다 (배치 분석용, 결과는 ida.log 로).
        var psi = new ProcessStartInfo(idat)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-A", "-Lida.log", $"-Srun_finder.py {region.Name}", "ffxiv_dx11.exe" },
        };
        psi.Environment["TVHEADLESS"] = "1";
        foreach (string name in Proc.SecretEnv) psi.Environment.Remove(name); // GitHub 토큰은 IDA 스크립트에 넘기지 않는다
        Log.Note($"[opcode] IDA 분석 중 (수 분 걸림): {Path.GetFileName(idat)} -> {workDir}");
        var sw = Stopwatch.StartNew();
        try
        {
            // 보통 4~5분 — 라이선스 창 등으로 멈추면 끝없이 기다리지 않게
            using var p = Process.Start(psi) ?? throw new ToolException($"{idat} 실행에 실패했습니다.");
            Proc.WaitOrKill(p, TimeSpan.FromMinutes(30), "IDA");
        }
        finally { File.Delete(exe); }

        string? result = Directory.Exists(Path.Combine(workDir, "output"))
            ? Directory.GetFiles(Path.Combine(workDir, "output"), "opcodes.json", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (result == null)
            throw new ToolException($@"IDA 분석 결과에 opcodes.json 이 없습니다. {Path.Combine(workDir, "ida.log")} 를 확인하세요.");
        File.Copy(result, Path.Combine(workDir, CacheName), overwrite: true);
        Files.WriteAtomic(Path.Combine(workDir, ScriptCache.StampFile), hash);
        Log.Note($"[opcode] IDA 완료 ({sw.Elapsed:m\\:ss})");
        // 다 됐으면 받아 둔 결과와 바꾼다 (분석을 다시 하지 않게, 바꾸지 못하면 이번 결과만)
        string cache = ScriptCache.Commit(workDir, dest, CacheName, "IDA");
        return new OpcodeSet(source, OpcodeSources.ParseIpcJson(File.ReadAllText(cache), cache, aliases: null));
    }

    private static Dictionary<string, string> Fetch()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in YarukonFiles)
        {
            string url = $"{YarukonRaw}/{f}";
            result[f] = Http.GetString(url);
        }
        return result;
    }
}
