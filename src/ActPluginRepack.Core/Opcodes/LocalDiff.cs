using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

// xivdev/opcodediff 를 로컬에서 실행해 직전 버전 -> 이번 버전 opcode 대응표를 만든다 (기본 켜짐, config "localDiff": false 로 끔).
// 스크립트·radare2·Python 패키지는 받아서 쓴다. Python 3.12+ 가 설치되어 있으면(py 런처, 또는 config "python") 그것을, 없으면
// 휴대용 Python(nuget.org 의 Python Software Foundation 공식 패키지, 버전·SHA512 고정)을 tools\python\ 에 받아 압축만 풀어 쓴다.
// IDA 방식(항목마다 시그니처)과 달리 패킷 핸들러 시그니처 2개만 쓰고 나머지는 두 exe 의 핸들러를 맞대어
// 찾으므로, IDA 시그니처가 어긋났을 때의 대안이 된다. upstream CI(minor_patch_diff.yml)와 같은 조합으로 돌린다:
//   - vtable_diff.py (minor_patch_diff.py 는 7.57 에서 분기문 시그니처가 안 맞음)
//   - radare2 는 CI 가 쓰는 버전 그대로 (6.1.6 은 case 를 2개 덜 읽어 실패, 6.1.0 은 공개 diff 와 동일)
//   - Python 3.12+ (Windows 에서 r2pipe 출력 동기화가 os.set_blocking 에 의존 — 3.11 은 멈춘다)
// 옛 exe(직전 출시, OpcodeCollector 가 Thaliak.PreviousRelease 로 고름): snapshots\<지역>\<그 버전>\ 이 있으면 그것, 없으면
// 그 지역의 Thaliak 패치 저장소에서 받아 스냅샷으로 남긴다 (Thaliak.SnapshotExe). 패치 번호는 필요 없다 — 스크립트는 두 exe 를
// 순서(옛 것, 새 것)로만 받는다.
internal static class LocalDiff
{
    private const string Raw = "https://raw.githubusercontent.com/xivdev/opcodediff/master";
    private static readonly string[] Scripts =
        ["vtable_diff.py", "analysis_utils.py", "utils.py", "requirements-vtable.txt"];
    private const string DefaultRadare2 = "6.1.0";
    // 휴대용 Python: 공개 저장소가 늦을 때의 마지막 수단이라 Python 이 없는 사람도 돌릴 수 있게. 버전을 고정하고 받은 파일은
    // SHA512 로 확인한다 (nuget.org 의 기록과 같은 값 — 고정 버전의 패키지는 바뀌지 않으므로 손볼 일이 없다).
    private const string PortablePython = "3.13.16";
    private const string PortablePythonSha512 =
        "4c6121b914e95ad4627c3478aff18486683f70632ab7007890e0a4b754215e70a67431370e272dde22c9235c6bb0d4916d71b20b02c485eeaa6b945ddacdfbeb";

    // 옛 게임 버전(previous)의 exe -> 지금 exe 의 opcode 대응표 (옛 값 -> 새 값)
    public static Dictionary<int, int> Run(Workspace ws, GameClient client, string previous, string? pythonPath, bool overwrite)
    {
        var region = client.Region!;
        // 옛 버전마다 따로 캐시 (옛 버전 판단이 바뀌면 다시 돌리게)
        string work = Path.Combine(ws.SnapshotDir(region, client.Version!), "localdiff", previous);
        string cache = Path.Combine(work, "diff.json");
        string what = $"localdiff {region.Name} {previous} -> {client.Version}";
        // 스크립트는 저장소 최신본을 쓴다 — 메이저 패치 당일에 고쳐지는 일이 잦으므로, 바뀌었으면 캐시를 쓰지 않고 다시 돌린다
        // (받기는 ETag 로 바뀌었는지만 묻는다. 해시를 남기기 전에 만든 결과도 다시 돌린다). 받지 못하면(오프라인 등) 캐시가 있으면 그것을 쓴다.
        // 새로 돌리는 것은 옆 폴더에서 — 끝까지 성공해야 받아 둔 결과를 바꾼다 (바뀐 스크립트가 아직 깨져 있거나 pip·radare2 를
        // 받지 못해도 어제 쓰던 결과로 계속 고칠 수 있게)
        return ScriptCache.Run(work, cache, overwrite, trustUnstamped: false,
            fetch: () => Scripts.ToDictionary(f => f, f => Http.GetBytes($"{Raw}/{f}")),
            hash: scripts => Files.Sha256Hex(scripts.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value).ToArray()),
            readCache: () => OpcodeSources.ParseDiff(File.ReadAllText(cache), what),
            fresh: (scripts, scriptHash) => Fresh(ws, client, previous, pythonPath, scripts, scriptHash, work, what),
            new ScriptCache.Notes(
                FetchFailed: why => $"[opcode] localdiff 스크립트를 받지 못해 ({why}) 받아 둔 결과 사용: {cache}",
                Cached: $"[opcode] localdiff 결과 캐시 사용: {cache}",
                Changed: "[opcode] opcodediff 스크립트가 바뀌어 localdiff 를 다시 돌림",
                RunFailed: why => $"[opcode] 바뀐 스크립트로 localdiff 를 다시 돌리지 못해 받아 둔 결과를 씀 - {why}"));
    }

    private static Dictionary<int, int> Fresh(Workspace ws, GameClient client, string previous, string? pythonPath,
                                              Dictionary<string, byte[]> scripts, string scriptHash, string dest, string what)
    {
        var region = client.Region!;
        string work = ScriptCache.NewDir(dest);
        foreach (var (f, bytes) in scripts)
        {
            File.WriteAllBytes(Path.Combine(work, Path.GetFileName(f)), bytes);
            // 저장소 최신본을 그대로 실행하므로 어느 것을 실행했는지 남긴다 (문제가 생겼을 때 추적용)
            Log.Note($"[opcode] {f} (opcodediff master) sha256 {Files.Sha256Hex(bytes)[..16]}");
        }

        string python = EnsureVenv(ws, pythonPath, File.ReadAllText(Path.Combine(work, "requirements-vtable.txt")));
        string r2bin = EnsureRadare2(ws);
        string prevExe = Thaliak.SnapshotExe(ws, region, previous);

        string oldCopy = Path.Combine(work, "ffxiv_dx11.old.exe");
        string newCopy = Path.Combine(work, "ffxiv_dx11.new.exe");
        File.Copy(prevExe, oldCopy);
        File.Copy(client.ExePath, newCopy);
        try
        {
            Log.Note($"[opcode] {what} 실행 중");
            var sw = Stopwatch.StartNew();
            // r2pipe 출력 동기화(utils.sync_r2_output)는 radare2 가 1초 안에 떠야 성공한다 — exe 가 디스크
            // 캐시에 없으면 가끔 늦으므로 그 실패만 몇 번 다시 시도한다.
            (int Code, string Stdout, string Stderr) r = (-1, "", "");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                // 정상이면 몇 초 — 멈추는 경우(Python 3.11, 콘솔 없음)를 위해 10분 제한
                // TERM 이 있으면(Git Bash 등) radare2 의 출력이 r2pipe 와 어긋나 "never got synced" 로 실패한다
                r = Proc.Run(python, ["vtable_diff.py", oldCopy, newCopy], work, r2bin, TimeSpan.FromMinutes(10), unsetEnv: ["TERM"]);
                KillStrayRadare2(r2bin);
                if (!r.Stderr.Contains("never got synced")) break;
                Log.Note($"[opcode] radare2 동기화 실패 - 다시 시도 ({attempt}/3)");
            }
            File.WriteAllText(Path.Combine(work, "vtable_diff.log"), r.Stderr);
            if (r.Code != 0 || !r.Stdout.TrimStart().StartsWith('['))
            {
                string hint = r.Stderr.Contains("never got synced") ? " radare2 와 r2pipe 가 통신하지 못했습니다. 다시 시도하세요." : "";
                throw new ToolException($"{what} 이(가) 실패했습니다 (종료 코드 {r.Code}).{hint} " +
                                        $"자세한 내용은 {Path.Combine(work, "vtable_diff.log")} 에 있습니다. 마지막 출력: {Proc.LastLine(r.Stderr)}");
            }
            Files.WriteAtomic(Path.Combine(work, "diff.json"), r.Stdout);
            Files.WriteAtomic(Path.Combine(work, ScriptCache.StampFile), scriptHash);
            Log.Note($"[opcode] localdiff 완료 ({sw.Elapsed:m\\:ss})");
        }
        finally
        {
            KillStrayRadare2(r2bin);
            // exe 사본은 크니 지우되, 늦게 끝난 radare2 가 잡고 있어도 계산한 결과는 잃지 않게
            foreach (string copy in new[] { oldCopy, newCopy }) Files.TryDeleteFile(copy);
        }
        // 다 됐으면 받아 둔 결과와 바꾼다 (바꾸지 못하면 이번 결과만 쓰고, 받아 둔 것은 그대로)
        string result = ScriptCache.Commit(work, dest, "diff.json", "localdiff");
        return OpcodeSources.ParseDiff(File.ReadAllText(result), what);
    }

    // 이 클라이언트 버전에 받아 둔 localdiff 결과의 옛 출시 버전들 (가까운 것부터) — 공식 패치 목록을 받지 못할 때
    public static List<string> CachedPrevious(Workspace ws, GameClient client)
    {
        string dir = Path.Combine(ws.SnapshotDir(client.Region!, client.Version!), "localdiff");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>()
            .Where(v => GameVersion.IsValid(v) && File.Exists(Path.Combine(dir, v, "diff.json")))
            .OrderByDescending(v => v, StringComparer.Ordinal).ToList();
    }

    // 돌릴 수 있는지. Python 이 없어도 휴대용 Python 을 받아 쓰므로, 설정에 적은 Python 경로가 틀린 경우만 false
    // (예전에 만든 가상환경이 있어도 — 설정이 바뀌면 EnsureVenv 가 다시 만든다).
    public static bool HasPython(string? pythonPath) => pythonPath == null || File.Exists(pythonPath);

    // 설치된 Python 3.12+ (설정한 경로, 또는 py 런처). 없으면 null — 그때는 휴대용 Python 을 받는다.
    public static string? InstalledPython(string? pythonPath) =>
        pythonPath != null ? (File.Exists(pythonPath) ? pythonPath : null) : FindPython312(AppContext.BaseDirectory);

    // 설치된 Python 이 없을 때 쓸 휴대용 Python (tools\python\<버전>\python.exe)
    private static string EnsurePortablePython(Workspace ws)
    {
        string dir = Path.Combine(ws.Tool("python"), PortablePython);
        string exe = Path.Combine(dir, "python.exe");
        if (File.Exists(exe)) return exe;
        string url = $"https://api.nuget.org/v3-flatcontainer/python/{PortablePython}/python.{PortablePython}.nupkg";
        Log.Note($"[opcode] 설치된 Python 3.12+ 가 없어 휴대용 Python {PortablePython} 받는 중 (약 14MB): {url}");
        byte[] pkg = Http.GetBytes(url);
        if (!Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(pkg)).Equals(PortablePythonSha512, StringComparison.Ordinal))
            throw new ToolException("받은 휴대용 Python 이 예상한 파일과 다릅니다 (SHA512 불일치). 다시 시도하세요.");
        // 패키지의 tools\ 가 Python 그대로다. 임시 폴더에 다 푼 뒤 옮긴다 (중간에 끊겨 반쪽 폴더가 "있음"으로 보이지 않게)
        Files.InstallDirectory(dir, tmp =>
        {
            using (var zip = new ZipArchive(new MemoryStream(pkg)))
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("tools/", StringComparison.Ordinal) && e.Name.Length > 0))
                {
                    string dst = Path.GetFullPath(Path.Combine(tmp, e.FullName["tools/".Length..]));
                    if (!dst.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    e.ExtractToFile(dst);
                }
            if (!File.Exists(Path.Combine(tmp, "python.exe"))) throw new ToolException($"{url} 안에 python.exe 가 없습니다.");
        });
        Files.DeleteOthers(ws.Tool("python"), dir, "opcode");
        return exe;
    }

    private const string RequirementsMarker = "requirements-vtable.txt";
    private const string BaseMarker = "base-python.txt"; // 설정에 적은 Python 경로 (자동으로 찾았으면 빈 파일)

    // tools\opcodediff-venv : requirements 나 설정한 Python 이 바뀌면 다시 설치한다.
    private static string EnsureVenv(Workspace ws, string? pythonPath, string requirements)
    {
        string venv = ws.Tool("opcodediff-venv");
        string py = Path.Combine(venv, "Scripts", "python.exe");
        string marker = Path.Combine(venv, RequirementsMarker);
        if (VenvUpToDate(venv, pythonPath, requirements)) return py;
        // 설정이 이 가상환경 안의 python 을 가리키면 다시 만들 때 스스로를 지우게 된다
        if (pythonPath != null && Path.GetFullPath(pythonPath).StartsWith(Path.GetFullPath(venv) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"python 설정이 localdiff 가상환경 안의 python.exe 를 가리킵니다 ({pythonPath}). 비우거나 설치된 Python 의 python.exe 를 적으세요.");

        string basePython = pythonPath ?? FindPython312(ws.Root) ?? EnsurePortablePython(ws);
        // 설정에 적은 Python 도 3.12+ 여야 한다 (3.11 은 오류 없이 멈춘다)
        var pv = Proc.Run(basePython, ["--version"], ws.Root, null, TimeSpan.FromMinutes(1));
        if (Regex.Match(pv.Stdout + pv.Stderr, @"Python (\d+)\.(\d+)") is not { Success: true } vm ||
            (int.Parse(vm.Groups[1].Value), int.Parse(vm.Groups[2].Value)) is var (major, minor) && (major < 3 || (major == 3 && minor < 12)))
            throw new ToolException($"localdiff 에는 Python 3.12 이상이 필요합니다 ({basePython}: {(pv.Stdout + pv.Stderr).Trim()}).");
        Log.Note($"[opcode] localdiff 용 Python 환경 준비: {venv} ({basePython})");
        if (Directory.Exists(venv)) Directory.Delete(venv, recursive: true);
        Proc.Check(Proc.Run(basePython, ["-m", "venv", venv], ws.Root, null), "python -m venv");
        File.WriteAllText(Path.Combine(venv, BaseMarker), ConfiguredBase(pythonPath));
        File.WriteAllText(marker + ".tmp", requirements);
        Proc.Check(Proc.Run(py, ["-m", "pip", "install", "-q", "--disable-pip-version-check", "-r", marker + ".tmp"], ws.Root, null),
              "pip install");
        File.Move(marker + ".tmp", marker, overwrite: true);
        return py;
    }

    // 그대로 써도 되는 가상환경인지: 다 만들어졌고(requirements 표시는 pip 이 끝난 뒤에 쓴다), requirements 와 설정한 Python 이
    // 같고, 만든 Python(pyvenv.cfg 의 home)이 아직 있다(휴대용 Python 버전 교체 등으로 없어지면 다시 만든다).
    // 설정이 없으면(자동) 자동으로 만든 것은 그대로 쓴다 — py 런처에 새 Python 이 생겼다고 다시 만들지는 않는다.
    internal static bool VenvUpToDate(string venv, string? pythonPath, string requirements)
    {
        string marker = Path.Combine(venv, RequirementsMarker);
        if (!File.Exists(Path.Combine(venv, "Scripts", "python.exe")) || !File.Exists(marker) || File.ReadAllText(marker) != requirements)
            return false;
        string baseMarker = Path.Combine(venv, BaseMarker);
        string built = File.Exists(baseMarker) ? File.ReadAllText(baseMarker).Trim() : ""; // 표시가 없는 예전 것은 자동으로 본다
        if (!built.Equals(ConfiguredBase(pythonPath), StringComparison.OrdinalIgnoreCase)) return false;
        string cfg = Path.Combine(venv, "pyvenv.cfg");
        if (!File.Exists(cfg)) return false;
        string? home = File.ReadLines(cfg).Select(l => l.Split('=', 2)).Where(kv => kv.Length == 2 && kv[0].Trim() == "home")
            .Select(kv => kv[1].Trim()).FirstOrDefault();
        return home != null && File.Exists(Path.Combine(home, "python.exe"));
    }

    private static string ConfiguredBase(string? pythonPath) => pythonPath == null ? "" : Path.GetFullPath(pythonPath);

    // py 런처 목록에서 3.12 이상 중 가장 높은 것 (시험용: ACTREPACK_PORTABLE_PYTHON=1 이면 없는 것으로 — 휴대용 경로 확인)
    private static string? FindPython312(string workDir)
    {
        if (Environment.GetEnvironmentVariable("ACTREPACK_PORTABLE_PYTHON") == "1") return null;
        try
        {
            var (code, stdout, _) = Proc.Run("py", ["-0p"], workDir, null);
            return code == 0 ? PickPython312(stdout, File.Exists) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // py 런처 없음
        }
    }

    // py -0p 출력(" -V:3.13 *        C:\...\python.exe")에서 3.12 이상 중 가장 높은, 실제 있는 python.exe
    internal static string? PickPython312(string pyList, Func<string, bool> exists) =>
        Regex.Matches(pyList, @"-V:(\d+)\.(\d+)\S*\s+\*?\s*(\S.*?python\.exe)", RegexOptions.IgnoreCase)
            .Select(m => (Major: int.Parse(m.Groups[1].Value), Minor: int.Parse(m.Groups[2].Value), Path: m.Groups[3].Value.Trim()))
            .Where(v => v.Major > 3 || (v.Major == 3 && v.Minor >= 12))
            .OrderByDescending(v => v.Major).ThenByDescending(v => v.Minor)
            .Select(v => v.Path).FirstOrDefault(exists);

    // tools\radare2\<버전>\ : upstream CI 워크플로가 쓰는 버전을 따른다.
    private static string EnsureRadare2(Workspace ws)
    {
        string ver = DefaultRadare2;
        string? yml = Http.GetStringOrNull($"{Raw}/.github/workflows/minor_patch_diff.yml");
        if (yml != null && Regex.Match(yml, @"radare2/releases/download/([0-9.]+)/") is { Success: true } m) ver = m.Groups[1].Value;

        string baseDir = ws.Tool("radare2");
        string dir = Path.Combine(baseDir, ver);
        string? bin = FindRadare2Bin(dir);
        if (bin == null)
        {
            string url = $"https://github.com/radareorg/radare2/releases/download/{ver}/radare2-{ver}-w64.zip";
            Log.Note($"[opcode] radare2 {ver} 받는 중: {url}");
            byte[] zip = Http.GetBytes(url);
            // 임시 폴더에 다 푼 뒤 옮긴다 — 중간에 끊기면 radare2.exe 만 있고 DLL 이 빠진 폴더가 "설치됨"으로 보이므로
            Files.InstallDirectory(dir, tmp =>
            {
                ZipFile.ExtractToDirectory(new MemoryStream(zip), tmp);
                if (FindRadare2Bin(tmp) == null) throw new ToolException($"{url} 안에 radare2.exe 가 없습니다.");
            });
            bin = FindRadare2Bin(dir)!;
        }
        Files.DeleteOthers(baseDir, dir, "opcode");
        return bin;
    }

    // 스크립트가 예외로 끝나면 r2pipe 가 띄운 radare2 가 남아 exe 를 잡고 있다. 우리 tools\radare2 의
    // radare2 만 정리한다 (사용자가 따로 쓰는 radare2 는 건드리지 않음).
    private static void KillStrayRadare2(string r2bin)
    {
        foreach (var p in Process.GetProcessesByName("radare2"))
        {
            try
            {
                if (p.MainModule?.FileName is { } exe && exe.StartsWith(r2bin, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
    }

    private static string? FindRadare2Bin(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "radare2.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
            : null;
}
