using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

// ACT 에 설치 / 원래대로 되돌리기.
//
// 설치 위치는 ACT 설정(%APPDATA%\Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml)의 플러그인 목록에서
// 찾는다. 해루봇도 이 방식으로 등록한다(Plugins\FFXIV_ACT_Plugin_KR 등 언어별 폴더).
//   - FFXIV 플러그인: output\ 의 DLL 들을 FFXIV_ACT_Plugin.dll 이 있는 폴더에 제자리 교체. 해루봇은 ACT 를 실행할 때마다
//     ACT 설정을 그 폴더의 FFXIV_ACT_Plugin.dll 로 되돌리므로 다른 곳에 두면 안 된다.
//   - OverlayPlugin(있으면): extras\OverlayPlugin\opcodes.jsonc 를 OverlayPlugin.dll 옆 resources\ 에. 해루봇은 시작할
//     때마다 이 파일을 OverlayPlugin 배포본으로 덮어쓰지만, ACT 를 직접 쓰면 그대로 적용된다.
// 해루봇에서 플러그인을 "업데이트"하면 플러그인 폴더를 통째로 지우고 공식본을 다시 받으므로 백업은 프로젝트의
// backups\ 에 둔다.
//
// deucalion: 플러그인은 시작할 때 같은 폴더의 deucalion-*.distrib.dll 이 있으면 기존 deucalion-*.dll 을 지우고 이름을
// 바꿔 쓴다. 그래서 패치본은 distrib 이름으로 넣고, 옛 .dll 은 백업한 뒤 치운다. 게임이 deucalion 을 잡고 있으면
// 교체가 안 되므로 ACT 와 게임이 모두 꺼져 있어야 설치한다.
internal sealed record ActTarget(string PluginPath, bool Enabled)
{
    public string PluginDir => Path.GetDirectoryName(PluginPath)!;
    public string? InstalledVersion => MaterialState.PluginVersionOf(PluginPath);
}

// 설치할 곳 (관리자 권한으로 다시 실행할 때 그대로 넘긴다).
// Official: 패치본(output\) 대신 input\plugin 의 공식 플러그인을 그대로 설치 — 공식 최신본이 이미 지금 게임과 맞는데
// ACT 에는 옛 공식본이나 예전 패치본이 있을 때 (해루봇의 플러그인 업데이트와 같은 결과).
internal sealed record InstallPlan(ActTarget Plugin, string? OverlayResources, bool Official = false)
{
    // 관리자 권한으로 다시 실행할 때 CLI install 에 넘길 인자 (정한 대상을 그대로: "-" 는 OverlayPlugin 건너뜀)
    public string[] ToCliArgs(Workspace ws) =>
        ["--root", ws.Root, "--target", Plugin.PluginDir, "--overlay", OverlayResources ?? "-", .. (Official ? new[] { "--official" } : [])];
}

internal sealed record InstallResult(InstallPlan Plan, string BackupDir, IReadOnlyList<string> Files);

internal sealed record RestoreResult(string BackupDir, IReadOnlyList<string> Restored, IReadOnlyList<string> Removed);

internal static class Install
{
    public const string OverlayOpcodes = "opcodes.jsonc";

    // 환경 변수 ACTREPACK_ACT_CONFIG 로 다른 ACT 설정 파일을 쓸 수 있다 (시험용 — 진짜 ACT 를 건드리지 않고 GUI 설치까지 돌려 보기)
    public static string ActConfigPath =>
        Environment.GetEnvironmentVariable("ACTREPACK_ACT_CONFIG") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "Config",
                       "Advanced Combat Tracker.config.xml");

    // ACT 설정에 등록된 플러그인 중 파일 이름이 fileName 인 것 (켜진 것 먼저). ACT 가 없으면 빈 목록.
    private static List<ActTarget> FindPlugins(string fileName)
    {
        if (!File.Exists(ActConfigPath)) return [];
        XDocument doc;
        try { doc = XDocument.Load(ActConfigPath); }
        catch (System.Xml.XmlException ex) { throw new ActTargetException(ActProblem.ConfigUnreadable, $"ACT 설정 파일을 읽지 못했습니다 ({ActConfigPath}): {ex.Message}"); }
        return doc.Descendants("ActPlugins").Elements("Plugin")
            .Select(p => (Path: (string?)p.Attribute("Path"), Enabled: string.Equals((string?)p.Attribute("Enabled"), "True", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.Path != null && Path.GetFileName(p.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ActTarget(p.Path!, p.Enabled))
            .OrderByDescending(t => t.Enabled)
            .ToList();
    }

    // FFXIV 플러그인 위치: 직접 준 폴더, 아니면 ACT 설정에서 켜진 것
    public static ActTarget ResolveTarget(string? pluginDir)
    {
        if (pluginDir != null)
        {
            string path = Path.Combine(Path.GetFullPath(pluginDir), PluginRelease.PluginFile);
            // 관리자 권한 재실행도 이 경로로 온다 — FFXIV 플러그인이 실제로 있는 폴더만 (엉뚱한 폴더에 쓰지 않게)
            if (!File.Exists(path)) throw new ToolException($"FFXIV_ACT_Plugin.dll 이 있는 폴더가 아닙니다: {pluginDir}");
            return new ActTarget(path, Enabled: true);
        }
        var targets = FindPlugins(PluginRelease.PluginFile);
        if (targets.Count == 0)
            throw new ActTargetException(ActProblem.NotRegistered, $"ACT 에 등록된 FFXIV_ACT_Plugin 을 찾지 못했습니다 ({ActConfigPath}). ACT 나 해루봇에서 플러그인을 한 번 설치한 뒤 다시 하거나, 플러그인 폴더를 직접 지정하세요.");
        var enabled = targets.Where(t => t.Enabled).ToList();
        if (enabled.Count > 1)
            throw new ActTargetException(ActProblem.MultipleEnabled, $"켜진 FFXIV_ACT_Plugin 이 여러 개입니다. 설치할 폴더를 직접 지정하세요: {string.Join(", ", enabled.Select(t => t.PluginDir))}",
                                         enabled.Select(t => t.PluginDir).ToList());
        var target = enabled.FirstOrDefault() ?? targets[0];
        if (!File.Exists(target.PluginPath))
            throw new ActTargetException(ActProblem.FileMissing, $"ACT 설정에 적힌 플러그인 파일이 없습니다: {target.PluginPath}");
        return target;
    }

    // OverlayPlugin 의 resources 폴더 (켜진 OverlayPlugin.dll 옆). 없으면 null.
    public static string? ResolveOverlayResources()
    {
        var overlay = FindPlugins("OverlayPlugin.dll").FirstOrDefault(t => t.Enabled && File.Exists(t.PluginPath));
        string? dir = overlay != null ? Path.Combine(overlay.PluginDir, "resources") : null;
        return dir != null && Directory.Exists(dir) ? dir : null;
    }

    // OverlayPlugin.dll 옆의 resources 폴더인지 (관리자 권한 재실행으로 받은 경로도 확인)
    internal static bool IsOverlayResources(string dir) =>
        Path.GetFileName(dir).Equals("resources", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? "", "OverlayPlugin.dll"));

    // 관리자 권한으로 쓰기 전에: 대상 폴더나 그 위 폴더가 정션·심볼릭 링크면 거부한다 (확인 창이 떠 있는 사이에 다른 폴더로
    // 바꿔치기하지 못하게)
    internal static void RejectReparsePoint(string dir)
    {
        if (!Elevation.IsElevated) return;
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d != null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ToolException($"대상 폴더({dir})의 경로에 다른 곳을 가리키는 링크(정션)가 있어 관리자 권한으로 쓰지 않습니다: {d.FullName}");
    }

    // 이미 설치된 패치본에 다른 대상만 다시 넣는다. 지금 그 자리에 있는 파일이 우리가 넣은 것이 아니면(OverlayPlugin 이 업데이트되며
    // 바뀐 opcodes.jsonc, 처음 설치 때 없던 대상 등) 먼저 그 백업에 원래 파일로 보탠다 — 되돌리면 지금 것으로 돌아가게.
    private static InstallResult Reinstall(InstallPlan plan, List<Change> pending, string backup, BackupManifest m,
                                           Dictionary<string, string> sourceHash)
    {
        foreach (var c in pending)
        {
            RejectReparsePoint(c.Dir);
            Elevation.EnsureWritable(c.Dir);
        }
        var targets = m.Targets.ToList();
        foreach (var c in pending)
        {
            int i = targets.FindIndex(t => SamePath(t.Dir, c.Dir));
            var t = i >= 0 ? targets[i] : new BackupManifest.Target(c.Dir, NextStore(backup, targets), [], []) { Hashes = [] };
            string store = Path.Combine(backup, t.Store);
            Directory.CreateDirectory(store);
            var existed = t.Existed.ToList();
            var added = t.Added.ToList();
            var hashes = new Dictionary<string, string>(t.Hashes ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var (source, name) in c.Put)
            {
                string f = Path.Combine(c.Dir, name);
                bool ours = hashes.TryGetValue(name, out string? h) && File.Exists(f) && Sha256Of(f) == h;
                if (File.Exists(f) && !ours)
                {
                    File.Copy(f, Path.Combine(store, name), overwrite: true);
                    ClearReadOnly(Path.Combine(store, name));
                    if (!existed.Contains(name, StringComparer.OrdinalIgnoreCase)) existed.Add(name);
                    added.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                }
                else if (!File.Exists(f) && !existed.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                         !added.Contains(name, StringComparer.OrdinalIgnoreCase))
                    added.Add(name);
                hashes[name] = sourceHash[source];
            }
            t = t with { Existed = existed, Added = added, Hashes = hashes };
            if (i >= 0) targets[i] = t; else targets.Add(t);
        }
        (m with { Targets = targets }).Save(backup);

        var again = new List<string>();
        foreach (var c in pending)
            foreach (var (source, name) in c.Put)
            {
                ReplaceFile(source, Path.Combine(c.Dir, name));
                again.Add(Path.Combine(c.Dir, name));
                Log.Info($"[install] {name} -> {c.Dir}");
            }
        Log.Done($"[install] 플러그인은 이미 설치되어 있어 바뀐 파일만 다시 넣음 (원래 파일 백업: {backup})");
        return new InstallResult(plan, backup, again);
    }

    private static string NextStore(string backup, List<BackupManifest.Target> targets)
    {
        int n = targets.Count;
        while (targets.Any(t => t.Store == n.ToString()) || Directory.Exists(Path.Combine(backup, n.ToString()))) n++;
        return n.ToString();
    }

    private static bool SamePath(string a, string b) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    // 이 대상에 넣을 파일이 모두 같은 내용으로 이미 있는지 (distrib 은 플러그인이 시작할 때 이름을 바꿨을 수 있다 — 치울 옛 deucalion
    // 이 그 바뀐 이름뿐이면 이미 설치된 것)
    private static bool AlreadyInstalled(Change c, Dictionary<string, string> sourceHash)
    {
        var renamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, name) in c.Put)
        {
            string hash = sourceHash[source];
            string? hit = new[] { name, RenamedDistrib(name) }.OfType<string>()
                .FirstOrDefault(n => File.Exists(Path.Combine(c.Dir, n)) && Sha256Of(Path.Combine(c.Dir, n)) == hash);
            if (hit == null) return false;
            if (!hit.Equals(name, StringComparison.OrdinalIgnoreCase)) renamed.Add(hit);
        }
        return c.Remove.All(renamed.Contains);
    }

    // 무엇을 어디에 설치할지. pluginDir / overlayResources 를 주면 그대로 쓴다 (관리자 권한 재실행 때).
    // overlayResources 가 "-" 면 OverlayPlugin 은 건너뛴다.
    public static InstallPlan Plan(Workspace ws, string? pluginDir = null, string? overlayResources = null, bool official = false)
    {
        if (official)
        {
            if (!File.Exists(ws.PluginPath))
                throw new ToolException(@"input\plugin 에 공식 플러그인이 없습니다. 먼저 다시 확인(prepare)하세요.");
            var target = ResolveTarget(pluginDir);
            // ACT 쪽이 더 새 버전이면 옛 공식본으로 내리지 않는다
            if (Version.TryParse(target.InstalledVersion, out var have) && Version.TryParse(MaterialState.PluginVersionOf(ws.PluginPath), out var latest) &&
                have > latest)
                throw new ToolException($"ACT 에 더 새 플러그인({have})이 설치되어 있어 공식 플러그인 {latest} 버전으로 바꾸지 않습니다.");
            return new InstallPlan(target, null, Official: true); // OverlayPlugin 은 건드리지 않는다
        }
        if (!File.Exists(ws.OutputPluginPath))
            throw new ToolException(@"output\ 에 빌드 결과가 없습니다. 먼저 build 를 실행하세요.");
        var plugin = ResolveTarget(pluginDir);
        // 옛 베이스로 만든 패치본으로 ACT 의 더 새 플러그인을 덮지 않는다 (공식 플러그인 받기가 막혀 옛 베이스로
        // 빌드했는데 해루봇은 이미 새 공식본을 받은 경우 등). 이 프로그램이 설치한 패치본은 같은 버전 번호를 쓴다.
        if (Version.TryParse(plugin.InstalledVersion, out var installed) &&
            Version.TryParse(MaterialState.PluginVersionOf(ws.OutputPluginPath), out var built) && installed > built)
            throw new ToolException($"ACT 에 더 새 플러그인({installed})이 설치되어 있어, 옛 공식 플러그인({built})으로 만든 패치본으로 덮어쓰지 않습니다. " +
                                    "다시 확인(prepare)해 최신 공식 플러그인으로 다시 만드세요.");
        string? overlay = null;
        if (File.Exists(ws.OverlayOpcodesPath) && overlayResources != "-")
        {
            overlay = overlayResources != null ? Path.GetFullPath(overlayResources) : ResolveOverlayResources();
            if (overlay != null && !IsOverlayResources(overlay))
                throw new ToolException($"OverlayPlugin.dll 옆의 resources 폴더가 아닙니다: {overlay}");
            if (overlay == null) Log.Note(@"[install] ACT 에 OverlayPlugin 이 없어 extras\OverlayPlugin\opcodes.jsonc 는 건너뜀");
        }
        return new InstallPlan(plugin, overlay);
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

    public static InstallResult Run(Workspace ws, InstallPlan plan)
    {
        string output = ws.OutputDir;
        string sourceDir = plan.Official ? Path.GetDirectoryName(ws.PluginPath)! : output;
        EnsureClosed();

        var changes = new List<Change>();
        var dlls = Directory.GetFiles(sourceDir, "*.dll").Select(f => (Source: f, Name: Path.GetFileName(f))).ToList();
        // 옛 deucalion 은 이름이 버전마다 다르다 — 들어 있는 것 전부 (distrib 와 이미 이름이 바뀐 .dll).
        // 새 deucalion 을 넣을 때만 치운다: 넣을 것에 deucalion 이 없으면(백신 격리 등) ACT 가 패킷을 못 받게 되므로 멈춘다.
        var oldDeucalion = Directory.GetFiles(plan.Plugin.PluginDir, "deucalion*.dll").Select(Path.GetFileName).Select(n => n!)
            .Where(n => !dlls.Any(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!dlls.Any(d => d.Name.StartsWith("deucalion", StringComparison.OrdinalIgnoreCase)))
            throw new ToolException($"설치할 파일에 deucalion 이 없습니다 ({sourceDir}). 백신이 격리했을 수 있습니다. " +
                                    "Windows 보안 > 바이러스 및 위협 방지 > 보호 기록에서 복원하거나 작업 폴더를 제외 목록에 넣은 뒤 다시 확인(prepare)하세요 (공식 플러그인을 다시 받습니다).");
        changes.Add(new Change(plan.Plugin.PluginDir, dlls, oldDeucalion));
        if (plan.OverlayResources != null)
            changes.Add(new Change(plan.OverlayResources, [(ws.OverlayOpcodesPath, OverlayOpcodes)], []));

        // 이미 같은 파일이 그대로 들어 있으면 아무것도 하지 않는다 — 백업을 새로 만들면 [설치 전으로 되돌리기]가 같은 파일로
        // "되돌리게" 된다 (같은 패치본을 다시 설치하거나 공식 최신본이 이미 있을 때)
        var sourceHash = changes.SelectMany(c => c.Put).Select(p => p.Source).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(s => s, Sha256Of, StringComparer.OrdinalIgnoreCase); // 넣을 파일의 해시는 한 번만
        var pending = changes.Where(c => !AlreadyInstalled(c, sourceHash)).ToList();
        if (pending.Count == 0)
        {
            Log.Done("[install] 같은 파일이 이미 설치되어 있어 바꾸지 않음");
            return new InstallResult(plan, "", []);
        }
        // 플러그인은 이미 이 패치본이고 다른 대상(해루봇이 시작할 때마다 덮어쓰는 opcodes.jsonc 등)만 다를 때: 이 패치본을 설치한
        // 백업에 그 대상을 보태고 다시 넣는다 (새 백업을 만들면 패치본이 "원래 파일"로 들어가 되돌리기가 망가진다)
        if (!pending.Contains(changes[0]) && LatestBackup(ws) is { } last && BackupManifest.Load(last) is { } lm &&
            lm.Targets.Any(t => SamePath(t.Dir, changes[0].Dir) &&
                                changes[0].Put.All(p => t.Hashes?.GetValueOrDefault(p.Name) == sourceHash[p.Source])))
            return Reinstall(plan, pending, last, lm, sourceHash);

        foreach (var c in changes)
        {
            RejectReparsePoint(c.Dir);
            Elevation.EnsureWritable(c.Dir); // 아무것도 바꾸기 전에
        }

        // 백업: 바꾸거나 지울 파일의 원본과, 원래 없던 파일 목록 (되돌릴 때 지운다), 넣은 파일의 해시 (되돌리기 전에
        // ACT 쪽이 그 뒤로 바뀌지 않았는지 확인)
        string backup = NewBackupDir(ws);
        var targets = new List<BackupManifest.Target>();
        BackupManifest manifest;
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
                ClearReadOnly(Path.Combine(store, name)); // 원본이 읽기 전용이어도 백업은 나중에 정리할 수 있게
                existed.Add(name);
            }
            targets.Add(new BackupManifest.Target(c.Dir, i.ToString(), existed,
                c.Put.Select(p => p.Name).Where(n => !existed.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                Hashes = c.Put.ToDictionary(p => p.Name, p => sourceHash[p.Source], StringComparer.OrdinalIgnoreCase),
            });
        }
        manifest = new BackupManifest(DateTime.Now, plan.Plugin.InstalledVersion, targets, plan.Official ? null : BuildInfo.Load(output));
        manifest.Save(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 아무것도 바꾸기 전 — 기록 없이 반쯤 만든 백업 폴더는 정리할 길이 없으니 지운다
            try { Directory.Delete(backup, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw new ToolException($"원래 파일을 백업하지 못해 설치하지 않았습니다. 디스크 공간과 파일이 잠겨 있지 않은지 확인하세요. ({ex.Message})");
        }
        Log.Info($@"[install] 원래 파일 백업: {backup}");

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
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
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
            {
                // 반쯤 설치된 상태 — 다음 되돌리기는 "설치한 그 파일인지" 확인 없이 원래 파일로 되돌려야 한다
                try { (manifest with { FailedInstall = true }).Save(backup); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw new ToolException($"설치 중 파일을 쓰지 못했고, 원래대로 되돌리는 것도 일부 실패했습니다 ({string.Join(", ", failed)}). " +
                                        $"ACT 와 게임을 끈 뒤 되돌리기(화면의 [설치 전으로 되돌리기], 명령줄의 restore)를 다시 하세요. 백업: {backup} ({ex.Message})");
            }
            manifest.MarkRestored(backup);
            throw new ToolException($"설치 중 파일을 쓰지 못해 원래대로 되돌렸습니다. ACT 나 게임, 백신이 파일을 쓰고 있지 않은지, 파일이 읽기 전용이 아닌지 확인하세요. ({ex.Message})");
        }
        Log.Done("[install] 설치 완료 - ACT 를 실행하면 적용됨. 되돌리려면 restore");
        if (plan.OverlayResources != null)
            Log.Note("[install] 해루봇은 시작할 때마다 OverlayPlugin 의 opcodes.jsonc 를 OverlayPlugin 배포본으로 덮어씀 (ACT 를 직접 쓰면 유지)");
        PruneBackups(ws, keep: 10);
        return new InstallResult(plan, backup, written);
    }

    // 가장 최근 백업(또는 지정한 백업)으로 되돌린다. 설치한 뒤로 ACT 쪽 파일이 다른 것으로 바뀌었으면(해루봇의 플러그인
    // 업데이트 등) 되돌리지 않는다 — 되돌리면 지금 것보다 옛 플러그인으로 내려간다.
    public static RestoreResult Restore(Workspace ws, string? backupDir = null)
    {
        string dir = backupDir ?? LatestBackup(ws) ?? throw new ToolException(@"backups\ 에 되돌릴 백업이 없습니다.");
        var m = BackupManifest.Load(dir) ?? throw new ToolException($"백업 기록이 없거나 깨졌습니다: {dir}");
        m.Validate(dir);
        var recorded = m; // 되돌렸다고 적을 때는 건너뛴 대상도 그대로 남긴다
        // 그 뒤로 폴더째 지운 곳(OverlayPlugin 을 지움 등)은 되돌릴 것이 없다
        foreach (var gone in m.Targets.Where(t => !Directory.Exists(t.Dir)))
            Log.Note($"[restore] {gone.Dir} 이(가) 없어 건너뜀");
        m = m with { Targets = m.Targets.Where(t => Directory.Exists(t.Dir)).ToList() };
        if (m.Targets.Count == 0) throw new ToolException($"백업 기록의 폴더가 모두 없어져 되돌릴 곳이 없습니다: {dir}");
        // 되돌릴 수 없는 상태면 관리자 권한을 청하기 전에 알린다 (확인 창을 띄운 뒤 거절하지 않게)
        if (CannotRestoreReason(m, dir) is { } reason) throw new ToolException(reason);
        EnsureClosed();
        foreach (var t in m.Targets) Elevation.EnsureWritable(t.Dir);
        var restored = new List<string>();
        var removed = new List<string>();
        var failed = RestoreFrom(dir, m, checkCurrent: !m.FailedInstall, restored, removed);
        if (failed.Count > 0)
            throw new ToolException($"일부 파일을 되돌리지 못했습니다: {string.Join(", ", failed)}. ACT 와 게임을 끈 뒤 다시 시도하세요.");
        recorded.MarkRestored(dir);
        return new RestoreResult(dir, restored, removed);
    }

    // 이 백업으로 되돌리면 안 되는 이유 (되돌릴 수 있으면 null). 설치한 뒤로 ACT 의 플러그인이 다른 것으로 바뀌었으면(해루봇의
    // 플러그인 업데이트 등) 되돌리면 옛 버전으로 내려간다. 설치가 도중에 실패한 백업은 확인 없이 되돌린다.
    // 이미 원래 파일로 돌아가 있는 것(되돌리기가 도중에 실패했거나, 설치가 파일을 바꾸기 전에 멈춤)은 바뀐 것으로 보지 않는다.
    public static string? CannotRestoreReason(BackupManifest m, string backupDir)
    {
        if (m.FailedInstall) return null;
        foreach (var t in m.Targets)
        {
            if (t.Hashes?.GetValueOrDefault(PluginRelease.PluginFile) is not { } hash) continue;
            string f = Path.Combine(t.Dir, PluginRelease.PluginFile);
            if (File.Exists(f) && Sha256Of(f) != hash && !SameAsBackup(backupDir, t, PluginRelease.PluginFile))
                return "설치한 뒤로 ACT 의 플러그인이 다른 것으로 바뀌어(해루봇의 플러그인 업데이트 등) 되돌리지 않습니다. " +
                       $"되돌리면 지금보다 옛 플러그인으로 내려갑니다. ({f})";
        }
        return null;
    }

    // 지금 되돌릴 수 있는 백업 (가장 최근 것이 되돌릴 수 없으면 없음 — 화면의 되돌리기 버튼을 숨긴다)
    public static string? RestorableBackup(Workspace ws) => Restorable(ws).Dir;

    // 되돌릴 백업과, 없으면 그 이유 (가장 최근 백업을 쓸 수 없는 이유, 아니면 설치한 적이 없거나 이미 되돌림)
    public static (string? Dir, string? Reason) Restorable(Workspace ws)
    {
        if (LatestBackup(ws) is not { } dir || BackupManifest.Load(dir) is not { } m)
            return (null, "되돌릴 백업이 없습니다. 이 프로그램으로 설치한 적이 없거나, 이미 설치 전으로 되돌렸습니다.");
        try { m.Validate(dir); }
        catch (ToolException ex) { return (null, $"가장 최근 백업을 쓸 수 없습니다. {ex.Message}"); }
        if (m.Targets.All(t => !Directory.Exists(t.Dir)))
            return (null, $"가장 최근 백업이 가리키는 ACT 폴더가 모두 없어져 되돌릴 곳이 없습니다. ({dir})");
        return CannotRestoreReason(m, dir) is { } reason ? (null, reason) : (dir, null);
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
    private static List<string> RestoreFrom(string backup, BackupManifest m, bool checkCurrent,
                                            List<string>? restored = null, List<string>? removed = null)
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
            foreach (string name in changed) Log.Note($"[restore] {Path.Combine(t.Dir, name)} 은(는) 설치한 뒤로 바뀌어 그대로 둠");
            foreach (string name in t.Added.Where(n => !changed.Contains(n)))
            {
                // 플러그인은 시작할 때 deucalion-x.distrib.dll 을 deucalion-x.dll 로 이름을 바꾼다 — 바뀐 이름도 (같은 파일일 때만) 지운다
                foreach (string candidate in new[] { name, RenamedDistrib(name) }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string f = Path.Combine(t.Dir, candidate);
                    if (!File.Exists(f)) continue;
                    // 우리가 넣은 그 파일일 때만 지운다 (바뀐 이름도 같은 내용이어야)
                    if (t.Hashes?.GetValueOrDefault(name) is not { } h || Sha256Of(f) != h) continue;
                    if (t.Existed.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue; // 아래에서 원본으로 덮는다
                    try { DeleteFile(f); removed?.Add(f); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(f); }
                }
            }
            foreach (string name in t.Existed.Where(n => !changed.Contains(n)))
            {
                string dst = Path.Combine(t.Dir, name);
                try
                {
                    ReplaceFile(Path.Combine(backup, t.Store, name), dst);
                    restored?.Add(dst);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(dst); }
            }
            Log.Done($"[restore] {t.Dir} ({t.Existed.Count}개 복원, {t.Added.Count}개 제거)");
        }
        return failed;
    }

    // 설치했던 파일이 그대로 있는지 (distrib 은 플러그인이 이름을 바꿨을 수 있다)
    private static bool StillInstalled(string dir, string name, string hash) =>
        new[] { name, RenamedDistrib(name) }.Where(n => n != null)
            .Select(n => Path.Combine(dir, n!)).Any(f => File.Exists(f) && Sha256Of(f) == hash);

    // ACT 쪽 파일이 백업해 둔 원래 파일과 같은지 (이미 되돌려져 있음)
    private static bool SameAsBackup(string backup, BackupManifest.Target t, string name)
    {
        string f = Path.Combine(t.Dir, name), copy = Path.Combine(backup, t.Store, name);
        return t.Existed.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(f) && File.Exists(copy) &&
               Sha256Of(f) == Sha256Of(copy);
    }

    private static bool Present(string dir, string name) =>
        new[] { name, RenamedDistrib(name) }.Where(n => n != null).Any(n => File.Exists(Path.Combine(dir, n!)));

    private static string? RenamedDistrib(string name) =>
        name.EndsWith(".distrib.dll", StringComparison.OrdinalIgnoreCase) ? name[..^".distrib.dll".Length] + ".dll" : null;

    // 백업 기록(Hashes)은 대문자로 적어 왔다
    private static string Sha256Of(string path) => Files.Sha256Of(path).ToUpperInvariant();

    // dst 를 source 로 바꾼다 (옆에 .tmp 로 복사한 뒤 이름 바꾸기 — 실패하면 .tmp 를 남기지 않는다)
    private static void ReplaceFile(string source, string dst)
    {
        ClearReadOnly(dst);
        string tmp = dst + ".tmp";
        try
        {
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void ClearReadOnly(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    private static void DeleteFile(string path)
    {
        ClearReadOnly(path);
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
    // 다시 쓰지 않으므로 먼저 지운다 (설치·되돌리기를 여러 번 해도 공식 플러그인이 든 가장 오래된 백업이 남게)
    private static void PruneBackups(Workspace ws, int keep)
    {
        try
        {
            foreach (string d in Directory.GetDirectories(ws.BackupsDir)
                         .Select(d => (Dir: d, M: BackupManifest.Load(d))).Where(x => x.M != null)
                         .OrderBy(x => x.M!.RestoredAt != null).ThenByDescending(x => x.M!.InstalledAt).Skip(keep).Select(x => x.Dir))
            {
                // 예전 백업 사본의 읽기 전용 풀기 (링크는 따라가지 않는다 — 관리자 권한으로 돌 때 엉뚱한 파일을 건드리지 않게)
                foreach (string file in Directory.GetFiles(d, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    ClearReadOnly(file);
                Directory.Delete(d, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Note($"[install] 오래된 백업을 정리하지 못함: {ex.Message}");
        }
    }
}

// backups\<시각>\backup.json — 어디에 무엇을 설치했고, 원래 무엇이 있었는지. 원본은 backups\<시각>\<Store>\ 에.
internal sealed record BackupManifest(DateTime InstalledAt, string? OriginalPluginVersion, List<BackupManifest.Target> Targets,
                                      BuildInfo? Installed)
{
    // Hashes: 설치한 파일 이름 -> SHA256 (되돌리기 전에 ACT 쪽이 그대로인지 확인; 예전 기록에는 없음)
    public sealed record Target(string Dir, string Store, List<string> Existed, List<string> Added)
    {
        public Dictionary<string, string>? Hashes { get; init; }
    }

    public DateTime? RestoredAt { get; init; } // 되돌렸으면 그 시각 (다시 되돌리지 않게)
    public bool FailedInstall { get; init; }    // 설치가 도중에 실패해 일부만 바뀐 채 남음 (되돌릴 때 "설치한 그 파일인지" 확인하지 않음)

    public const string FileName = "backup.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string dir) => Files.WriteAtomic(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json));

    public void MarkRestored(string dir)
    {
        try { (this with { RestoredAt = DateTime.Now }).Save(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Note($"[restore] 백업 기록을 고치지 못함: {ex.Message}"); }
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
            bool overlayDir = Install.IsOverlayResources(dir);
            Install.RejectReparsePoint(dir);
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

    public static BackupManifest? Load(string dir)
    {
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json);
            return m?.Targets is { Count: > 0 } ? m : null; // 형식이 다른(예전) 기록은 없는 것으로
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
