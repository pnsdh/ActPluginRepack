using System.Diagnostics;
using System.Text.Json;

// ACT 에 설치 / 원래대로 되돌리기.
//
// 설치 위치는 ACT 설정(%APPDATA%\Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml)의 플러그인 목록에서
// 찾는다(ActLocator). 해루봇도 이 방식으로 등록한다(Plugins\FFXIV_ACT_Plugin_KR 등 언어별 폴더).
//   - FFXIV 플러그인: output\ 의 DLL 들을 FFXIV_ACT_Plugin.dll 이 있는 폴더에 제자리 교체. 해루봇은 ACT 를 실행할 때마다
//     ACT 설정을 그 폴더의 FFXIV_ACT_Plugin.dll 로 되돌리므로 다른 곳에 두면 안 된다.
//   - 해루봇은 FFXIV 플러그인을 언어마다 Plugins\FFXIV_ACT_Plugin_KR / _Global / _CN 에 따로 두고, ACT 를 실행할 때 고른
//     언어의 폴더로 ACT 설정을 바꾼다. KR / Global 은 같은 공식 플러그인이라 함께 설치한다(설치한 뒤 언어를 바꿔도 패치본이
//     쓰이게). _CN 은 중국판에 맞춰 고친 다른 플러그인(TundraWork/FFXIV_ACT_Plugin_CN)이라 건드리지 않는다.
//   - OverlayPlugin(있으면): extras\OverlayPlugin\opcodes.jsonc 를 OverlayPlugin.dll 옆 resources\ 에. 해루봇은 시작할
//     때마다 이 파일을 OverlayPlugin 배포본으로 덮어쓰지만, ACT 를 직접 쓰면 그대로 적용된다.
// 해루봇에서 플러그인을 "업데이트"하면 플러그인 폴더를 통째로 지우고 공식본을 다시 받으므로 백업은 프로젝트의
// backups\ 에 둔다.
//
// deucalion: 플러그인은 시작할 때 같은 폴더의 deucalion-*.distrib.dll 이 있으면 기존 deucalion-*.dll 을 지우고 이름을
// 바꿔 쓴다. 그래서 패치본은 distrib 이름으로 넣고, 옛 .dll 은 백업한 뒤 치운다. 게임이 deucalion 을 잡고 있으면
// 교체가 안 되므로 ACT 와 게임이 모두 꺼져 있어야 설치한다.

// 설치할 곳 (관리자 권한으로 다시 실행할 때 그대로 넘긴다).
// Official: 패치본(output\) 대신 input\plugin 의 공식 플러그인을 그대로 설치 — 공식 최신본이 이미 지금 게임과 맞는데
// ACT 에는 옛 공식본이나 예전 패치본이 있을 때 (해루봇의 플러그인 업데이트와 같은 결과).
internal sealed record InstallPlan(ActTarget Plugin, string? OverlayResources, bool Official = false)
{
    // 관리자 권한으로 다시 실행할 때 CLI install 에 넘길 인자 (정한 대상을 그대로: "-" 는 OverlayPlugin 건너뜀)
    public string[] ToCliArgs(Workspace ws) =>
        ["--root", ws.Root, .. Plugin.PluginDirs.SelectMany(d => new[] { "--target", d }), "--overlay", OverlayResources ?? "-",
         .. (Official ? new[] { "--official" } : [])];
}

internal sealed record InstallResult(InstallPlan Plan, string BackupDir, IReadOnlyList<string> Files);

// Skipped: 그 뒤로 바뀌어 그대로 둔 폴더·파일 (해루봇이 그 언어 플러그인만 업데이트함 등)
internal sealed record RestoreResult(string BackupDir, IReadOnlyList<string> Skipped);

internal static class Install
{
    public const string OverlayOpcodes = "opcodes.jsonc";

    // 관리자 권한으로 쓰기 전에: 대상 폴더나 그 위 폴더가 정션·심볼릭 링크면 거부한다 (확인 창이 떠 있는 사이에 다른 폴더로
    // 바꿔치기하지 못하게)
    internal static void RejectReparsePoint(string dir)
    {
        if (!Elevation.IsElevated) return;
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d != null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ToolException($"대상 폴더({dir})의 경로에 다른 곳을 가리키는 링크(정션)가 있어 관리자 권한으로 쓰지 않습니다: {d.FullName}");
    }

    // 이 대상에 넣을 파일이 모두 같은 내용으로 이미 있는지 (distrib 은 플러그인이 시작할 때 이름을 바꿨을 수 있다 — 치울 옛 deucalion
    // 이 그 바뀐 이름뿐이면 이미 설치된 것)
    private static bool AlreadyInstalled(Change c, Dictionary<string, string> sourceHash)
    {
        var renamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, name) in c.Put)
        {
            string hash = sourceHash[source];
            string? hit = new[] { name, RenamedDistrib(name) }.OfType<string>()
                .FirstOrDefault(n => File.Exists(Path.Combine(c.Dir, n)) && SameHash(Sha256Of(Path.Combine(c.Dir, n)), hash));
            if (hit == null) return false;
            if (!hit.Equals(name, StringComparison.OrdinalIgnoreCase)) renamed.Add(hit);
        }
        return c.Remove.All(renamed.Contains);
    }

    // 무엇을 어디에 설치할지. pluginDirs / overlayResources 를 주면 그대로 쓴다 (관리자 권한 재실행 때).
    // overlayResources 가 "-" 면 OverlayPlugin 은 건너뛴다.
    public static InstallPlan Plan(Workspace ws, IReadOnlyList<string>? pluginDirs = null, string? overlayResources = null, bool official = false)
    {
        if (official)
        {
            if (!File.Exists(ws.PluginPath))
                throw new ToolException("받아 둔 공식 플러그인이 없습니다" + Errors.CliOnly(@" (input\plugin)") + ". 먼저 " +
                                        Errors.CliOr("prepare 를 실행하세요.", "다시 확인하세요."));
            var target = ActLocator.ResolveTarget(pluginDirs);
            // ACT 쪽이 더 새 버전이면 옛 공식본으로 내리지 않는다
            ThrowIfNewer(target, ws.PluginPath);
            return new InstallPlan(WithoutNewer(target, ws.PluginPath), null, Official: true); // OverlayPlugin 은 건드리지 않는다
        }
        if (!File.Exists(ws.OutputPluginPath))
            throw new ToolException(@"output\ 에 빌드 결과가 없습니다. 먼저 build 를 실행하세요.");
        var plugin = ActLocator.ResolveTarget(pluginDirs);
        // 옛 베이스로 만든 패치본으로 ACT 의 더 새 플러그인을 덮지 않는다 (공식 플러그인 받기가 막혀 옛 베이스로
        // 빌드했는데 해루봇은 이미 새 공식본을 받은 경우 등). 이 프로그램이 설치한 패치본은 같은 버전 번호를 쓴다.
        // (해루봇이 공식 저장소보다 새 한국 임시본을 받아 둔 경우 등 — 공식 플러그인이 갱신될 때까지 다시 해도 같다)
        ThrowIfNewer(plugin, ws.OutputPluginPath);
        plugin = WithoutNewer(plugin, ws.OutputPluginPath);
        string? overlay = null;
        if (File.Exists(ws.OverlayOpcodesPath) && overlayResources != "-")
        {
            overlay = overlayResources != null ? Path.GetFullPath(overlayResources) : ActLocator.ResolveOverlayResources();
            if (overlay != null && !ActLocator.IsOverlayResources(overlay))
                throw new ToolException($"OverlayPlugin.dll 옆의 resources 폴더가 아닙니다: {overlay}");
            if (overlay == null) Log.Note(@"[install] ACT 에 OverlayPlugin 이 없어 extras\OverlayPlugin\opcodes.jsonc 는 건너뜀");
        }
        return new InstallPlan(plugin, overlay);
    }

    public static InstallPlan Plan(Workspace ws, string pluginDir, string? overlayResources = null, bool official = false) =>
        Plan(ws, [pluginDir], overlayResources, official);

    // ACT 설정이 가리키는 플러그인이 dll 보다 새 버전이면 그 버전 (아니면 null) — 설치는 덮어쓰지 않고, 상태·화면은 그렇다고 알린다
    public static string? NewerPluginIn(ActTarget act, string dll) =>
        Versions.IsNewer(act.PluginPath, dll) ? act.InstalledVersion : null;

    private static void ThrowIfNewer(ActTarget act, string dll)
    {
        if (NewerPluginIn(act, dll) is { } installed)
            throw new NewerPluginInstalledException(installed, MaterialState.PluginVersionOf(dll) ?? "?");
    }

    // 함께 설치할 다른 언어 폴더에 더 새 플러그인이 있으면 그 폴더는 뺀다 (해루봇에서 그 언어만 먼저 업데이트한 경우)
    private static ActTarget WithoutNewer(ActTarget target, string thanDll)
    {
        var than = Versions.OfAssembly(thanDll);
        var newer = target.Others.Where(d => Versions.IsNewer(Path.Combine(d, PluginRelease.PluginFile), than)).ToList();
        foreach (string d in newer) Log.Note($"[install] {d} 에는 더 새 플러그인이 있어 건너뜀");
        return newer.Count == 0 ? target : target with { Others = target.Others.Except(newer).ToList() };
    }

    // ACT 와 게임이 꺼져 있어야 한다 (파일이 잠겨 있고, deucalion 이 게임에 들어가 있음)
    public static void EnsureClosed()
    {
        var running = new List<string>();
        if (IsRunning("Advanced Combat Tracker")) running.Add("ACT");
        if (IsRunning("ffxiv_dx11")) running.Add("파이널판타지14");
        if (running.Count > 0) throw new AppsRunningException(running);
    }

    private static bool IsRunning(string name)
    {
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    // 한 폴더에 할 일: 넣을 파일(원본 경로 -> 이름)과 치울 파일
    private sealed record Change(string Dir, List<(string Source, string Name)> Put, List<string> Remove);

    // 플러그인 폴더마다 할 일: sourceDir 의 dll 을 넣고, 넣을 것에 없는 옛 deucalion(이름이 버전마다 다름)은 치운다
    private static List<Change> PluginChanges(string sourceDir, IEnumerable<string> pluginDirs)
    {
        var dlls = Directory.GetFiles(sourceDir, "*.dll").Select(f => (Source: f, Name: Path.GetFileName(f))).ToList();
        return pluginDirs.Select(dir => new Change(dir, dlls,
            Directory.GetFiles(dir, "deucalion*.dll").Select(Path.GetFileName).Select(n => n!)
                .Where(n => !dlls.Any(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList())).ToList();
    }

    private static Dictionary<string, string> SourceHashes(IEnumerable<Change> changes) =>
        changes.SelectMany(c => c.Put).Select(p => p.Source).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(s => s, Sha256Of, StringComparer.OrdinalIgnoreCase); // 넣을 파일의 해시는 한 번만

    // ACT 의 FFXIV 플러그인 폴더(설치가 넣을 곳 — 더 새 플러그인이 든 다른 언어 폴더는 뺀다)에 output\ 의 패치본이 그대로 들어
    // 있는지. 설치가 "같은 파일이 이미 있어 바꾸지 않음"으로 보는 것과 같은 판단이다 (deucalion 까지). OverlayPlugin opcodes.jsonc 는
    // 보지 않는다 — 해루봇이 시작할 때마다 덮어써서, 보면 [다시 확인하기]마다 다시 설치(관리자 권한 확인 창, 새 백업)하게 된다.
    public static bool PatchInPlace(Workspace ws, ActTarget act)
    {
        if (!File.Exists(ws.OutputPluginPath)) return false;
        var target = WithoutNewer(act, ws.OutputPluginPath);
        var changes = PluginChanges(ws.OutputDir, target.PluginDirs.Where(d => File.Exists(Path.Combine(d, PluginRelease.PluginFile))));
        var hashes = SourceHashes(changes);
        return changes.Count > 0 && changes.All(c => AlreadyInstalled(c, hashes));
    }

    public static InstallResult Run(Workspace ws, InstallPlan plan)
    {
        string output = ws.OutputDir;
        string sourceDir = plan.Official ? Path.GetDirectoryName(ws.PluginPath)! : output;
        EnsureClosed();

        // 옛 deucalion 은 이름이 버전마다 다르다 — 들어 있는 것 전부 (distrib 와 이미 이름이 바뀐 .dll).
        // 새 deucalion 을 넣을 때만 치운다: 넣을 것에 deucalion 이 없으면(백신 격리 등) ACT 가 패킷을 못 받게 되므로 멈춘다.
        if (!Directory.GetFiles(sourceDir, "deucalion*.dll").Any())
            throw new ToolException("설치할 파일에 deucalion 이 없습니다" + Errors.CliOnly($" ({sourceDir})") + ". 백신이 격리했을 수 있습니다. " +
                                    "Windows 보안 > 바이러스 및 위협 방지 > 보호 기록에서 복원하거나 작업 폴더를 제외 목록에 넣은 뒤 " +
                                    Errors.CliOr("prepare 부터 다시 하세요", "다시 시도하세요") + " (공식 플러그인을 다시 받습니다).");
        var changes = PluginChanges(sourceDir, plan.Plugin.PluginDirs);
        if (plan.OverlayResources != null)
            changes.Add(new Change(plan.OverlayResources, [(ws.OverlayOpcodesPath, OverlayOpcodes)], []));

        // 이미 같은 파일이 그대로 들어 있으면 아무것도 하지 않는다 — 백업을 새로 만들면 restore 가 같은 파일로
        // "되돌리게" 된다 (같은 패치본을 다시 설치하거나 공식 최신본이 이미 있을 때)
        var sourceHash = SourceHashes(changes);
        var pending = changes.Where(c => !AlreadyInstalled(c, sourceHash)).ToList();
        if (pending.Count == 0)
        {
            Log.Done("[install] 같은 파일이 이미 설치되어 있어 바꾸지 않음");
            return new InstallResult(plan, "", []);
        }
        // 이미 같은 파일이 든 대상은 백업하지도 바꾸지도 않는다 (해루봇이 덮어쓴 opcodes.jsonc, 나중에 생긴 다른 언어 폴더만
        // 다르면 그것만 새 백업에 담아 넣는다)
        changes = pending;
        foreach (var c in changes)
        {
            RejectReparsePoint(c.Dir);
            Elevation.EnsureWritable(c.Dir); // 아무것도 바꾸기 전에
        }

        var (backup, manifest) = CreateBackup(ws, changes, sourceHash, plan);
        Log.Info($@"[install] 원래 파일 백업: {backup}");
        var written = ApplyChanges(changes, backup, manifest);
        Log.Done("[install] 설치 완료 - ACT 를 실행하면 적용됨");
        if (plan.OverlayResources != null)
            Log.Note("[install] 해루봇은 시작할 때마다 OverlayPlugin 의 opcodes.jsonc 를 OverlayPlugin 배포본으로 덮어씀 (ACT 를 직접 쓰면 유지)");
        PruneBackups(ws, keep: 10);
        return new InstallResult(plan, backup, written);
    }

    // 백업: 바꾸거나 지울 파일의 원본과, 원래 없던 파일 목록 (되돌릴 때 지운다), 넣은 파일의 해시 (되돌리기 전에
    // ACT 쪽이 그 뒤로 바뀌지 않았는지 확인). 실패하면 아무것도 바꾸지 않고 ToolException.
    private static (string Dir, BackupManifest Manifest) CreateBackup(Workspace ws, List<Change> changes,
                                                                      Dictionary<string, string> sourceHash, InstallPlan plan)
    {
        string backup = NewBackupDir(ws);
        var targets = new List<BackupManifest.Target>();
        try
        {
            for (int i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                string store = Path.Combine(backup, i.ToString());
                Directory.CreateDirectory(store);
                var existed = new List<string>();
                foreach (string name in c.Put.Select(p => p.Name).Concat(c.Remove).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string f = Path.Combine(c.Dir, name);
                    if (!File.Exists(f)) continue;
                    File.Copy(f, Path.Combine(store, name));
                    Files.ClearReadOnly(Path.Combine(store, name)); // 원본이 읽기 전용이어도 백업은 나중에 정리할 수 있게
                    existed.Add(name);
                }
                targets.Add(new BackupManifest.Target(c.Dir, i.ToString(), existed,
                    c.Put.Select(p => p.Name).Where(n => !existed.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    Hashes = c.Put.ToDictionary(p => p.Name, p => sourceHash[p.Source], StringComparer.OrdinalIgnoreCase),
                });
            }
            var manifest = new BackupManifest(DateTime.Now, targets, plan.Official ? null : BuildInfo.Load(ws.OutputDir))
                { Official = plan.Official };
            manifest.Save(backup);
            return (backup, manifest);
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            // 아무것도 바꾸기 전 — 기록 없이 반쯤 만든 백업 폴더는 정리할 길이 없으니 지운다
            Files.DeleteDirectoryQuietly(backup);
            throw new ToolException($"원래 파일을 백업하지 못해 설치하지 않았습니다. 디스크 공간과 파일이 잠겨 있지 않은지 확인하세요. ({ex.Message})");
        }
    }

    // 파일을 바꾸고 넣은 파일 경로를 돌려준다. 도중에 실패하면 실제로 바꾼 파일만 백업으로 되돌린 뒤 ToolException.
    private static List<string> ApplyChanges(List<Change> changes, string backup, BackupManifest manifest)
    {
        var written = new List<string>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 실제로 지우거나 바꾼 파일
        try
        {
            foreach (var c in changes)
            {
                foreach (string name in c.Remove)
                {
                    DeleteFile(Path.Combine(c.Dir, name));
                    touched.Add(Path.Combine(c.Dir, name));
                }
                foreach (var (source, name) in c.Put)
                {
                    string dst = Path.Combine(c.Dir, name);
                    ReplaceFile(source, dst);
                    written.Add(dst);
                    touched.Add(dst);
                    Log.Info($"[install] {name} -> {c.Dir}");
                }
            }
            return written;
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            // 반쯤 바뀐 상태로 두지 않는다 — 방금 만든 기록(메모리)으로, 실제로 바꾼 파일만 되돌린다 (실패 원인인 잠긴 파일은
            // 손대지 않았으므로 다시 쓰지 않는다. 디스크의 기록은 다시 읽지 않는다)
            var undo = manifest with
            {
                Targets = manifest.Targets.Select(t => t with
                {
                    Existed = t.Existed.Where(n => touched.Contains(Path.Combine(t.Dir, n))).ToList(),
                    Added = t.Added.Where(n => touched.Contains(Path.Combine(t.Dir, n))).ToList(),
                }).ToList(),
            };
            var failed = RestoreFrom(backup, undo, checkCurrent: false);
            if (failed.Count > 0)
                throw new ToolException($"설치 중 파일을 쓰지 못했고, 원래대로 되돌리는 것도 일부 실패했습니다 ({string.Join(", ", failed)}). " +
                                        $"ACT 와 게임을 끈 뒤 화면의 [공식 플러그인으로 되돌리기](명령줄은 restore)를 누르거나, 해루봇에서 FFXIV 플러그인을 다시 받으세요. 백업: {backup} ({ex.Message})");
            manifest.MarkRestored(backup);
            throw new ToolException($"설치 중 파일을 쓰지 못해 원래대로 되돌렸습니다. ACT 나 게임, 백신이 파일을 쓰고 있지 않은지, 파일이 읽기 전용이 아닌지 확인하세요. ({ex.Message})");
        }
    }

    // 가장 최근 백업(또는 지정한 백업) 하나로 되돌린다 (CLI restore — 화면은 공식 플러그인을 다시 설치한다: Plan(official)).
    // 설치한 뒤로 ACT 쪽 파일이 다른 것으로 바뀌었으면(해루봇의 플러그인 업데이트 등) 되돌리지 않는다 — 옛 플러그인으로 내려간다.
    public static RestoreResult Restore(Workspace ws, string? backupDir = null)
    {
        string dir = backupDir ?? LatestBackup(ws) ?? throw new ToolException(@"backups\ 에 되돌릴 백업이 없습니다.");
        var m = BackupManifest.Load(dir) ?? throw new ToolException($"백업 기록이 없거나 깨졌습니다: {dir}");
        m.Validate(dir);
        var recorded = m; // 되돌렸다고 적을 때는 건너뛴 대상도 그대로 남긴다
        // 그 뒤로 폴더째 없어졌거나(OverlayPlugin 을 지움 등) 플러그인 파일이 없어진 곳(해루봇이 그 언어를 다시 받는 중 등)은
        // 되돌릴 것이 없어 건너뛴다
        var skipped = new List<string>();
        foreach (var gone in m.Targets.Where(Gone))
        {
            Log.Note($"[restore] {gone.Dir} 에 {(Directory.Exists(gone.Dir) ? "플러그인 파일이" : "폴더가")} 없어 건너뜀");
            skipped.Add(gone.Dir);
        }
        m = m with { Targets = m.Targets.Where(t => !Gone(t)).ToList() };
        if (m.Targets.Count == 0) throw new ToolException($"백업 기록의 폴더(또는 그 안의 플러그인)가 모두 없어져 되돌릴 곳이 없습니다: {dir}");
        // 되돌릴 수 없는 상태면 관리자 권한을 청하기 전에 알린다 (확인 창을 띄운 뒤 거절하지 않게)
        if (CannotRestoreReason(m, dir) is { } reason) throw new ToolException(reason);
        // 플러그인이 그 뒤로 바뀐 언어 폴더(해루봇에서 그 언어만 업데이트)는 그대로 두고 나머지만. 설치가 도중에 실패한 백업도
        // 같다 — 파일은 통째로 바꾸므로(임시 파일 -> 이름 바꾸기) 반쯤 쓴 파일은 없고, 우리가 넣은 것도 원래 것도 아니면 새 것이다.
        foreach (var t in m.Targets.Where(t => PluginReplaced(dir, t)))
        {
            Log.Note($"[restore] {t.Dir} 의 플러그인은 설치한 뒤로 바뀌어 그대로 둠");
            skipped.Add(t.Dir);
        }
        m = m with { Targets = m.Targets.Where(t => !PluginReplaced(dir, t)).ToList() };
        EnsureClosed();
        foreach (var t in m.Targets) Elevation.EnsureWritable(t.Dir);
        var failed = RestoreFrom(dir, m, checkCurrent: true, skipped);
        if (failed.Count > 0)
            throw new ToolException($"일부 파일을 되돌리지 못했습니다: {string.Join(", ", failed)}. ACT 와 게임을 끈 뒤 다시 시도하세요.");
        recorded.MarkRestored(dir);
        return new RestoreResult(dir, skipped);
    }

    // 되돌릴 수 없는 대상: 폴더가 없어졌거나, FFXIV 플러그인 대상인데 플러그인 파일이 없어짐 (그 폴더에는 아무것도 쓰지 않는다)
    internal static bool Gone(BackupManifest.Target t) =>
        !Directory.Exists(t.Dir) ||
        (t.Hashes?.ContainsKey(PluginRelease.PluginFile) == true && !File.Exists(Path.Combine(t.Dir, PluginRelease.PluginFile))) ||
        // OverlayPlugin 을 지웠거나 다시 받는 중 (resources\ 만 남음)
        (t.Hashes?.ContainsKey(OverlayOpcodes) == true && !ActLocator.IsOverlayResources(t.Dir));

    // 이 백업으로 되돌리면 안 되는 이유 (되돌릴 수 있으면 null). 설치한 뒤로 ACT 의 플러그인이 다른 것으로 바뀌었으면(해루봇의
    // 플러그인 업데이트 등) 되돌리면 옛 버전으로 내려간다. 설치가 도중에 실패한 백업도 같게 본다.
    // 이미 원래 파일로 돌아가 있는 것(되돌리기가 도중에 실패했거나, 설치가 파일을 바꾸기 전에 멈춤)은 바뀐 것으로 보지 않는다.
    // 플러그인 폴더가 여럿(해루봇의 언어별 폴더)이면 모두 바뀌었을 때만 — 일부만 바뀌었으면 나머지는 되돌린다.
    private static string? CannotRestoreReason(BackupManifest m, string backupDir)
    {
        var plugins = m.Targets.Where(t => t.Hashes?.ContainsKey(PluginRelease.PluginFile) == true && Directory.Exists(t.Dir)).ToList();
        if (plugins.Count > 0 && plugins.All(t => PluginReplaced(backupDir, t)))
            return "설치한 뒤로 ACT 의 플러그인이 다른 것으로 바뀌어(해루봇의 플러그인 업데이트 등) 되돌리지 않습니다. " +
                   $"되돌리면 지금보다 옛 플러그인으로 내려갑니다. ({Path.Combine(plugins[0].Dir, PluginRelease.PluginFile)})";
        return null;
    }

    // 이 대상의 FFXIV 플러그인이 설치한 뒤로 다른 것으로 바뀌었는지 (우리가 넣은 것도, 원래 파일도 아님)
    private static bool PluginReplaced(string backupDir, BackupManifest.Target t)
    {
        if (t.Hashes?.GetValueOrDefault(PluginRelease.PluginFile) is not { } hash) return false;
        string f = Path.Combine(t.Dir, PluginRelease.PluginFile);
        return File.Exists(f) && !SameHash(Sha256Of(f), hash) && !SameAsBackup(backupDir, t, PluginRelease.PluginFile);
    }

    // ACT 의 FFXIV 플러그인(언어 폴더 어느 것이든)이 이 도구가 설치한 패치본인지 — 백업 기록의 "넣은 파일" 해시로
    // (공식본 설치는 빼고). 화면의 [공식 플러그인으로 되돌리기]를 이때만 보인다.
    public static bool PatchInstalled(Workspace ws, ActTarget act)
    {
        if (!Directory.Exists(ws.BackupsDir)) return false;
        var ours = Directory.GetDirectories(ws.BackupsDir).Select(BackupManifest.Load)
            .Where(m => m is { InstalledOfficial: false })
            .SelectMany(m => m!.Targets).Select(t => t.Hashes?.GetValueOrDefault(PluginRelease.PluginFile)).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ours.Count > 0 && act.PluginPaths.Where(File.Exists).Any(f => ours.Contains(Sha256Of(f)));
    }

    // 아직 되돌리지 않은 가장 최근 백업
    public static string? LatestBackup(Workspace ws) =>
        Directory.Exists(ws.BackupsDir)
            ? Directory.GetDirectories(ws.BackupsDir)
                .Select(d => (Dir: d, M: BackupManifest.Load(d)))
                .Where(x => x.M != null && x.M.RestoredAt == null)
                .OrderByDescending(x => x.M!.InstalledAt).ThenByDescending(x => Path.GetFileName(x.Dir), StringComparer.Ordinal)
                .Select(x => x.Dir).FirstOrDefault()
            : null;

    // 백업을 되돌린다. 실패한 파일 경로를 돌려준다 (한 파일이 실패해도 나머지는 계속).
    // checkCurrent: 지금 ACT 쪽 파일이 설치했던 그 파일인지 먼저 확인 (설치 실패 직후의 되돌리기는 확인하지 않음)
    private static List<string> RestoreFrom(string backup, BackupManifest m, bool checkCurrent, List<string>? skipped = null)
    {
        // 설치한 뒤로 다른 것으로 바뀐 파일 (지금 있는데 우리가 넣은 것과도, 원래 파일과도 다름)
        HashSet<string> Changed(BackupManifest.Target t) => checkCurrent
            ? (t.Hashes ?? []).Where(kv => Present(t.Dir, kv.Key) && !StillInstalled(t.Dir, kv.Key, kv.Value) &&
                                           !SameAsBackup(backup, t, kv.Key))
                .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var failed = new List<string>();
        foreach (var t in m.Targets)
        {
            // 플러그인 외에 그 뒤로 바뀐 파일(해루봇이 시작할 때마다 덮어쓰는 OverlayPlugin opcodes.jsonc 등)은 그대로 둔다
            var changed = Changed(t);
            foreach (string name in changed)
            {
                Log.Note($"[restore] {Path.Combine(t.Dir, name)} 은(는) 설치한 뒤로 바뀌어 그대로 둠");
                skipped?.Add(Path.Combine(t.Dir, name));
            }
            int put = 0, deleted = 0;
            foreach (string name in t.Added.Where(n => !changed.Contains(n)))
            {
                // 플러그인은 시작할 때 deucalion-x.distrib.dll 을 deucalion-x.dll 로 이름을 바꾼다 — 바뀐 이름도 (같은 파일일 때만) 지운다
                foreach (string candidate in new[] { name, RenamedDistrib(name) }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string f = Path.Combine(t.Dir, candidate);
                    if (!File.Exists(f)) continue;
                    // 우리가 넣은 그 파일일 때만 지운다 (바뀐 이름도 같은 내용이어야)
                    if (t.Hashes?.GetValueOrDefault(name) is not { } h || !SameHash(Sha256Of(f), h)) continue;
                    if (t.Existed.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue; // 아래에서 원본으로 덮는다
                    try { DeleteFile(f); deleted++; }
                    catch (Exception ex) when (Errors.IsFileError(ex)) { failed.Add(f); }
                }
            }
            foreach (string name in t.Existed.Where(n => !changed.Contains(n)))
            {
                string dst = Path.Combine(t.Dir, name);
                try
                {
                    ReplaceFile(Path.Combine(backup, t.Store, name), dst);
                    put++;
                }
                catch (Exception ex) when (Errors.IsFileError(ex)) { failed.Add(dst); }
            }
            Log.Done($"[restore] {t.Dir} ({put}개 복원, {deleted}개 제거{(changed.Count > 0 ? $", {changed.Count}개 그대로 둠" : "")})");
        }
        return failed;
    }

    // 설치했던 파일이 그대로 있는지 (distrib 은 플러그인이 이름을 바꿨을 수 있다)
    private static bool StillInstalled(string dir, string name, string hash) =>
        new[] { name, RenamedDistrib(name) }.Where(n => n != null)
            .Select(n => Path.Combine(dir, n!)).Any(f => File.Exists(f) && SameHash(Sha256Of(f), hash));

    // ACT 쪽 파일이 백업해 둔 원래 파일과 같은지 (이미 되돌려져 있음)
    private static bool SameAsBackup(string backup, BackupManifest.Target t, string name)
    {
        string f = Path.Combine(t.Dir, name), copy = Path.Combine(backup, t.Store, name);
        return t.Existed.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(f) && File.Exists(copy) &&
               SameHash(Sha256Of(f), Sha256Of(copy));
    }

    private static bool Present(string dir, string name) =>
        new[] { name, RenamedDistrib(name) }.Where(n => n != null).Any(n => File.Exists(Path.Combine(dir, n!)));

    private static string? RenamedDistrib(string name) =>
        name.EndsWith(".distrib.dll", StringComparison.OrdinalIgnoreCase) ? name[..^".distrib.dll".Length] + ".dll" : null;

    // 백업 기록(Hashes)은 대문자로 적어 왔다 (backup.json 형식 — 비교는 대소문자를 가리지 않는다: SameHash)
    private static string Sha256Of(string path) => Files.Sha256Of(path).ToUpperInvariant();

    private static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // dst 를 source 로 바꾼다 (옆에 .tmp 로 복사한 뒤 이름 바꾸기 — 실패하면 .tmp 를 남기지 않는다)
    private static void ReplaceFile(string source, string dst)
    {
        Files.ClearReadOnly(dst);
        string tmp = dst + ".tmp";
        try
        {
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            Files.TryDeleteFile(tmp);
            throw;
        }
    }

    private static void DeleteFile(string path)
    {
        Files.ClearReadOnly(path);
        File.Delete(path);
    }

    // backups\<시각> — 같은 초에 두 번 설치해도 겹치지 않게
    private static string NewBackupDir(Workspace ws)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dir = Path.Combine(ws.BackupsDir, stamp);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(ws.BackupsDir, $"{stamp}-{i}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // 오래된 백업 정리 (설치할 때마다 수 MB 씩 쌓인다). 기록이 있는 백업 중 최근 keep 개만 남긴다 — 이미 되돌린 백업은
    // 다시 쓰지 않으므로 먼저 지운다
    private static void PruneBackups(Workspace ws, int keep)
    {
        try
        {
            var all = Directory.GetDirectories(ws.BackupsDir)
                .Select(d => (Dir: d, M: BackupManifest.Load(d))).Where(x => x.M != null).ToList();
            // 예전 백업 사본이 읽기 전용이어도 지운다 (DeleteDirectoryQuietly — 링크는 따라가지 않는다). 하나를 못 지워도 나머지는 계속
            foreach (string d in all.OrderBy(x => x.M!.RestoredAt != null).ThenByDescending(x => x.M!.InstalledAt)
                         .Skip(keep).Select(x => x.Dir))
                if (!Files.DeleteDirectoryQuietly(d)) Log.Note($"[install] 오래된 백업을 정리하지 못함: {d}");
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            Log.Note($"[install] 오래된 백업을 정리하지 못함: {ex.Message}");
        }
    }
}

// backups\<시각>\backup.json — 어디에 무엇을 설치했고, 원래 무엇이 있었는지. 원본은 backups\<시각>\<Store>\ 에.
internal sealed record BackupManifest(DateTime InstalledAt, List<BackupManifest.Target> Targets, BuildInfo? Installed)
{
    // Hashes: 설치한 파일 이름 -> SHA256 (되돌리기 전에 ACT 쪽이 그대로인지 확인; 예전 기록에는 없음)
    public sealed record Target(string Dir, string Store, List<string> Existed, List<string> Added)
    {
        public Dictionary<string, string>? Hashes { get; init; }
    }

    public DateTime? RestoredAt { get; init; } // 되돌렸으면 그 시각 (다시 되돌리지 않게)
    public bool? Official { get; init; }                // 패치본 대신 공식 최신본을 설치했음 (예전 기록에는 없음)

    // 공식 최신본을 설치한 백업인지 — 예전 기록은 빌드 기록이 없으면(공식본 설치는 빌드 기록을 남기지 않았다) 그렇게 본다
    [System.Text.Json.Serialization.JsonIgnore]
    public bool InstalledOfficial => Official ?? Installed == null;

    public const string FileName = "backup.json";

    public void Save(string dir) => Files.WriteAtomic(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, JsonOptions.Indented));

    public void MarkRestored(string dir)
    {
        try { (this with { RestoredAt = DateTime.Now }).Save(dir); }
        catch (Exception ex) when (Errors.IsFileError(ex)) { Log.Note($"[restore] 백업 기록을 고치지 못함: {ex.Message}"); }
    }

    // 백업 기록은 사용자가 쓸 수 있는 곳에 있고, 되돌리기는 관리자 권한으로 돌 수 있다 — 기록이 가리키는 곳이
    // ACT 의 플러그인 폴더 / OverlayPlugin resources 이고, 이름이 그 안의 파일 이름일 때만 따른다.
    public void Validate(string backupDir)
    {
        foreach (var t in Targets)
        {
            string dir = Path.GetFullPath(t.Dir);
            if (!Directory.Exists(dir)) continue; // 그 뒤로 폴더째 없어짐 — 되돌릴 때 건너뛴다 (아무것도 쓰지 않음)
            // 기록이 스스로 주장하는 내용이 아니라 실제 디스크로 판단한다
            bool pluginDir = File.Exists(Path.Combine(dir, PluginRelease.PluginFile));
            bool overlayDir = ActLocator.IsOverlayResources(dir);
            Install.RejectReparsePoint(dir);
            // 플러그인 대상인데 플러그인 파일이 없어짐 (해루봇이 그 언어를 다시 받는 중 등) — 되돌릴 때 건너뛴다 (아무것도 쓰지 않음)
            // OverlayPlugin 대상인데 OverlayPlugin.dll 이 없어짐도 같다 (Install.Gone 과 같은 조건 — 건너뛴 대상에는 쓰지 않는다)
            if (!pluginDir && !overlayDir && Install.Gone(t)) continue;
            if (!pluginDir && !overlayDir)
                throw new ToolException($"백업 기록이 ACT 플러그인 폴더가 아닌 곳을 가리킵니다: {t.Dir} ({backupDir})");
            foreach (string name in t.Existed.Concat(t.Added).Concat(t.Hashes?.Keys ?? Enumerable.Empty<string>()))
                if (name != Path.GetFileName(name) || name.Contains("..") ||
                    !(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.Equals(Install.OverlayOpcodes, StringComparison.OrdinalIgnoreCase)))
                    throw new ToolException($"백업 기록에 쓸 수 없는 파일 이름이 있습니다: {name} ({backupDir})");
            if (t.Store != Path.GetFileName(t.Store) || t.Store.Contains(".."))
                throw new ToolException($"백업 기록이 깨졌습니다: {backupDir}");
            // 넣은 파일의 해시가 없으면(예전 형식) 무엇을 지워도 되는지 알 수 없다. 원래 파일로 덮을 수 있는 것은 우리가 넣은 이름이거나
            // 설치 때 치운 옛 deucalion 뿐이다.
            if (t.Hashes == null || t.Added.Any(n => !t.Hashes.ContainsKey(n)) ||
                t.Existed.Any(n => !t.Hashes.ContainsKey(n) && !n.StartsWith("deucalion", StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"백업 기록이 예전 형식이거나 깨져 되돌릴 수 없습니다: {backupDir}. 해루봇이나 ACT 에서 플러그인을 다시 받으세요.");
            // 원래 파일의 사본이 없으면(백신이 deucalion 사본을 격리 등) 되돌리다 멈춰 ACT 에 deucalion 이 없는 채로 남는다 — 시작 전에 멈춘다
            if (t.Existed.FirstOrDefault(n => !File.Exists(Path.Combine(backupDir, t.Store, n))) is { } lost)
                throw new ToolException($"백업해 둔 원래 파일이 없습니다: {Path.Combine(backupDir, t.Store, lost)}. 백신이 격리했을 수 있습니다. " +
                                        "Windows 보안 > 바이러스 및 위협 방지 > 보호 기록에서 복원한 뒤 다시 시도하거나, 해루봇이나 ACT 에서 플러그인을 다시 받으세요.");
        }
    }

    // 이 백업의 기록. 없거나 읽지 못하면(깨짐, 잠김, 예전 형식) null
    public static BackupManifest? Load(string dir)
    {
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), JsonOptions.Indented);
            return m?.Targets is { Count: > 0 } ? m : null; // 형식이 다른(예전) 기록은 없는 것으로
        }
        catch (Exception ex) when (ex is JsonException || Errors.IsFileError(ex)) { return null; }
    }
}
