// 프로젝트 폴더와 그 안의 모든 경로. 경로는 여기서만 만든다.
//
//   input\plugin       베이스 플러그인 (+ deucalion, 부속 DLL)     ← prepare 가 최신 공식본으로 맞춘다
//   input\csv\<lang>   게임 데이터 CSV                              ← prepare
//   input\opcodes      Machina opcode                               ← prepare
//   input\overrides    스킬명 오버라이드                            ← 사용자
//   input\signatures   교체 시그니처                                ← prepare(자동 복구) / 사용자
//   input\.state.json  마지막 prepare 의 기록 (재료가 어느 게임/플러그인 버전 것인지)
//   config.json        사용자별 설정 (Config)
//   data\              CSV 에서 만든 리스트 (build 가 매번 다시 만듦)
//   output\            완성된 플러그인
//   snapshots\<지역>\<버전>\  게임 exe 보관 (다음 패치 때 비교용), opcodediff / IDA 캐시
//   extras\            다른 도구용·확인용 (lemegeton.xml / OverlayPlugin\opcodes.jsonc / Machina\<지역>.txt / sources.txt / crosscheck\)
//   tools\             받아 둔 외부 도구와 캐시
//   backups\<시각>\    ACT 에 설치하기 전 원래 파일 (install / restore)
//   hotfix\<패치>\     패치본 배포 묶음 (hotfix)
//   logs\              작업 기록 (작업마다 한 파일, 최근 TaskLog.Keep 개 — 문제 보고용 기록 묶음에 들어간다)
internal sealed record Workspace(string Root)
{
    public const string Marker = "build.ps1";

    public string Input => Path.Combine(Root, "input");
    public string PluginDir => Path.Combine(Input, "plugin");
    public string PluginPath => Path.Combine(PluginDir, PluginRelease.PluginFile);
    public string CsvDir => Path.Combine(Input, "csv");
    public string CsvLeafDir(string leaf) => Path.Combine(CsvDir, leaf);
    public string OpcodesDir => Path.Combine(Input, "opcodes");
    public string OverridesDir => Path.Combine(Input, "overrides");
    public string SignaturesDir => Path.Combine(Input, "signatures");
    public string StatePath => Path.Combine(Input, ".state.json");
    public string DataDir => Path.Combine(Root, "data");
    public string OutputDir => Path.Combine(Root, "output");
    public string OutputPluginPath => Path.Combine(OutputDir, PluginRelease.PluginFile);
    public string SnapshotsDir => Path.Combine(Root, "snapshots");
    public string ExtrasDir => Path.Combine(Root, "extras");
    public string LemegetonPath => Path.Combine(ExtrasDir, "lemegeton.xml");
    public string OverlayOpcodesPath => Path.Combine(ExtrasDir, "OverlayPlugin", Install.OverlayOpcodes);
    public string MachinaOpcodesDir => Path.Combine(ExtrasDir, "Machina");
    public string CrossCheckDir => Path.Combine(ExtrasDir, "crosscheck"); // 교차 확인한 출처마다의 opcode
    public string ToolsDir => Path.Combine(Root, "tools");
    public string BackupsDir => Path.Combine(Root, "backups");
    public string LogsDir => Path.Combine(Root, "logs");
    public string ConfigPath => Path.Combine(Root, Config.FileName);

    public string SnapshotRegionDir(Region region) => Path.Combine(SnapshotsDir, region.Name);
    public string SnapshotDir(Region region, string version) => Path.Combine(SnapshotRegionDir(region), version);
    public string HotfixDir(string tag) => Path.Combine(Root, "hotfix", tag);
    public string Tool(params string[] parts) => Path.Combine([ToolsDir, .. parts]);

    // GUI 사용자의 작업 폴더 (저장소가 없으므로): %LOCALAPPDATA%\ActPluginRepack. 없으면 만든다.
    public static Workspace ForUser()
    {
        var ws = new Workspace(AppPaths.UserRoot);
        ws.EnsureCreated();
        return ws;
    }

    // 재료 폴더 구조를 만든다 (있으면 그대로)
    public void EnsureCreated()
    {
        foreach (string d in new[] { PluginDir, CsvDir, OpcodesDir, OverridesDir, SignaturesDir })
            Directory.CreateDirectory(d);
    }

    // root 를 직접 주거나, 없으면 start(기본 현재 폴더)에서 위로 올라가며 build.ps1 + input\ 이 있는 폴더를 찾는다
    public static Workspace Find(string? root = null, string? start = null)
    {
        if (root != null) return new Workspace(Path.GetFullPath(root));
        for (string? d = start ?? Directory.GetCurrentDirectory(); d != null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, Marker)) && Directory.Exists(Path.Combine(d, "input")))
                return new Workspace(d);
        throw new ToolException("프로젝트 폴더를 찾지 못했습니다. 프로젝트 폴더 안에서 실행하거나 --root <dir> 로 지정하세요.");
    }

    // 같은 프로젝트 폴더에서 두 작업이 동시에 돌면 서로의 작업 폴더를 지우고 덮어쓰므로 막는다
    // 잠금 파일을 앞 작업이 막 놓아 지워지는 중이면(DeleteOnClose) 접근 거부가 나므로 잠깐 다시 해 본다.
    // 접근 거부가 처음 나면 폴더에 쓸 수 있는지 따로 보고(읽기 전용 폴더면 그렇다고 알림), 계속 거부되면 다른 작업이 잡고 있는 것으로 본다.
    public IDisposable Lock()
    {
        string path = Path.Combine(Root, ".actrepack.lock");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (DirectoryNotFoundException)
            {
                throw new ToolException($"작업 폴더({Root})가 없습니다. 경로를 확인하세요.");
            }
            catch (IOException) // 공유 위반: 다른 작업이 잡고 있음
            {
                throw new WorkspaceBusyException(Root);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == 0 && !Elevation.CanWrite(Root))
                    throw new ToolException($"작업 폴더({Root})에 쓸 수 없습니다. 폴더가 읽기 전용이거나 권한이 없는지 확인하세요.");
                if (attempt >= 10) throw new WorkspaceBusyException(Root);
                Thread.Sleep(100);
            }
        }
    }
}
