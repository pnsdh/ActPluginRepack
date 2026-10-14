namespace ActPluginRepack.Tests;

// 설치 도중 파일을 못 쓰면 ACT 쪽을 설치 전 그대로 되돌린다 (ElevationTests 와 같은 묶음: InstallTests 참고)
[Collection("elevation")]
public class InstallRollbackTests
{
    // output\ 에 패치본 셋(deucalion, 플러그인, 마지막에 복사될 zzz.dll), ACT 폴더에 옛 공식본
    private static (TempDir Tmp, Workspace Ws, string Plugin) Setup()
    {
        var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/output/FFXIV_ACT_Plugin.dll", "our patch");
        tmp.File("project/output/deucalion-1.5.1.distrib.dll", "new deucalion");
        tmp.File("project/output/zzz.dll", "new zzz");
        string plugin = tmp.Dir("act/Plugins/FFXIV_ACT_Plugin_KR");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", "old official");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/deucalion-1.5.0.dll", "old deucalion");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/keep.txt", "unrelated");
        return (tmp, ws, plugin);
    }

    // 폴더 안 파일·하위 폴더 이름과 내용
    private static string Snapshot(string dir) => string.Join("|",
        Directory.GetFileSystemEntries(dir).Order(StringComparer.OrdinalIgnoreCase).Select(f =>
            Directory.Exists(f) ? $"{Path.GetFileName(f)}/" : $"{Path.GetFileName(f)}={File.ReadAllText(f)}"));

    [Fact]
    public void FailedCopyRollsBackEverything()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            // zzz.dll 의 임시 파일 자리에 폴더가 있어 복사가 실패한다 (deucalion·플러그인은 이미 바뀐 뒤)
            Directory.CreateDirectory(Path.Combine(plugin, "zzz.dll.tmp"));
            string before = Snapshot(plugin);

            var ex = Assert.Throws<ToolException>(() => Install.Run(ws, Install.Plan(ws, plugin, "-")));
            Assert.Contains("되돌렸습니다", ex.Message);
            Assert.Equal(before, Snapshot(plugin));
            Assert.Null(Install.LatestBackup(ws)); // 되돌린 백업은 restore 대상이 아니다
        }
    }

    [Fact]
    public void LockedFileFailsAndOtherFilesAreRestored()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            string zzz = tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/zzz.dll", "old zzz");
            // 다른 프로그램이 읽기 공유로만 열고 있음: 백업(읽기)은 되지만 교체는 안 된다
            using (new FileStream(zzz, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<ToolException>(() => Install.Run(ws, Install.Plan(ws, plugin, "-")));

            Assert.Equal("old zzz", File.ReadAllText(zzz));
            Assert.Equal("old official", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
            Assert.Equal("old deucalion", File.ReadAllText(Path.Combine(plugin, "deucalion-1.5.0.dll")));
            Assert.False(File.Exists(Path.Combine(plugin, "deucalion-1.5.1.distrib.dll")));
        }
    }

    // 잠긴 파일 때문에 실패하면 실제로 바꾼 파일만 되돌린다 (잠긴 파일은 다시 쓰지 않으므로 되돌리기가 깨끗이 끝남)
    // -> 그 백업은 "되돌림"으로 표시되어 restore 대상이 아니다
    [Fact]
    public void FailedInstallRevertsOnlyTouchedFilesAndMarksBackupRestored()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            string zzz = tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/zzz.dll", "old zzz");
            string before = Snapshot(plugin);
            ToolException ex;
            using (new FileStream(zzz, FileMode.Open, FileAccess.Read, FileShare.Read))
                ex = Assert.Throws<ToolException>(() => Install.Run(ws, Install.Plan(ws, plugin, "-")));

            Assert.Contains("되돌렸습니다", ex.Message);                     // "일부 실패"가 아님 — 잠긴 zzz.dll 은 되돌리려 하지 않았다
            Assert.Equal(before, Snapshot(plugin));
            string backup = Assert.Single(Directory.GetDirectories(ws.BackupsDir));
            var m = BackupManifest.Load(backup)!;
            Assert.NotNull(m.RestoredAt);
            Assert.False(m.FailedInstall);
            Assert.Null(Install.LatestBackup(ws));
        }
    }

    // 일부를 되돌리지 못한 설치(FailedInstall)는 "설치한 그 파일인지" 확인 없이 원래 파일로 되돌린다.
    // 표시가 없으면 같은 상태(플러그인이 넣은 것과 다름)는 "다른 것으로 바뀜"으로 보고 되돌리지 않는다.
    [Fact]
    public void FailedInstallFlagLetsRestoreSkipTheHashCheck()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            var result = Install.Run(ws, Install.Plan(ws, plugin, "-"));
            string pluginFile = Path.Combine(plugin, "FFXIV_ACT_Plugin.dll");
            File.WriteAllText(pluginFile, "half written");                    // 넣은 것도 원래 것도 아님

            Assert.Null(Install.RestorableBackup(ws));
            Assert.Throws<ToolException>(() => Install.Restore(ws));

            var m = BackupManifest.Load(result.BackupDir)!;
            (m with { FailedInstall = true }).Save(result.BackupDir);
            Assert.Equal(result.BackupDir, Install.RestorableBackup(ws));
            Install.Restore(ws);
            Assert.Equal("old official", File.ReadAllText(pluginFile));
            Assert.Equal("old deucalion", File.ReadAllText(Path.Combine(plugin, "deucalion-1.5.0.dll")));
            Assert.False(File.Exists(Path.Combine(plugin, "deucalion-1.5.1.distrib.dll")));
        }
    }

    // 같은 파일이 이미 설치되어 있으면 아무것도 하지 않는다 — 새 백업을 만들지 않아 되돌리기는 처음 설치 전으로 간다
    [Fact]
    public void InstallingTheSameFilesAgainIsANoOp()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            var first = Install.Run(ws, Install.Plan(ws, plugin, "-"));
            string after = Snapshot(plugin);
            var again = Install.Run(ws, Install.Plan(ws, plugin, "-"));

            Assert.Equal("", again.BackupDir);
            Assert.Empty(again.Files);
            Assert.Equal(after, Snapshot(plugin));
            Assert.Single(Directory.GetDirectories(ws.BackupsDir));
            Assert.Equal(first.BackupDir, Install.LatestBackup(ws));
            Install.Restore(ws);
            Assert.Equal("old official", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
        }
    }

    // 설치한 뒤 플러그인이 다른 것으로 바뀌었으면(해루봇의 업데이트 등) 되돌릴 수 없다 — 관리자 권한을 청하기 전에 알 수 있게
    [Fact]
    public void RestorableBackupIsNullWhenPluginWasReplaced()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            var result = Install.Run(ws, Install.Plan(ws, plugin, "-"));
            Assert.Equal(result.BackupDir, Install.RestorableBackup(ws));

            File.WriteAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll"), "newer official");
            Assert.Null(Install.RestorableBackup(ws));
            Assert.Equal(result.BackupDir, Install.LatestBackup(ws));        // 백업은 있지만 되돌리면 안 됨
            var ex = Assert.Throws<ToolException>(() => Install.Restore(ws));
            Assert.Contains("바뀌어", ex.Message);
            Assert.Equal("newer official", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
        }
    }

    // --official: ACT 에 공식 최신본보다 새 플러그인이 있으면 낮추지 않는다
    [Fact]
    public void OfficialInstallRefusesToDowngrade()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/input/plugin/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.1"));
        string plugin = tmp.Dir("act/Plugins/FFXIV_ACT_Plugin_KR");
        string installed = tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3"));

        var ex = Assert.Throws<ToolException>(() => Install.Plan(ws, plugin, official: true));
        Assert.Contains("더 새 플러그인", ex.Message);

        // 같거나 옛 버전이면 공식본 설치 계획 (OverlayPlugin 은 건드리지 않음)
        File.WriteAllBytes(installed, FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.0"));
        var plan = Install.Plan(ws, plugin, official: true);
        Assert.True(plan.Official);
        Assert.Null(plan.OverlayResources);
    }

    // 패치본: 옛 공식본으로 만든 패치본으로 ACT 의 더 새 플러그인을 덮지 않는다
    [Fact]
    public void PatchedInstallRefusesToDowngrade()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/output/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.1", "patched"));
        string plugin = tmp.Dir("act/Plugins/FFXIV_ACT_Plugin_KR");
        string installed = tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3"));

        var ex = Assert.Throws<ToolException>(() => Install.Plan(ws, plugin, "-"));
        Assert.Contains("더 새 플러그인", ex.Message);

        // 같은 버전(이 프로그램이 설치한 패치본 포함)이거나 옛 버전이면 설치 계획
        File.WriteAllBytes(installed, FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.1"));
        Assert.False(Install.Plan(ws, plugin, "-").Official);
        File.WriteAllBytes(installed, FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.0"));
        Assert.False(Install.Plan(ws, plugin, "-").Official);
    }

    // 원본이 읽기 전용이어도 백업 사본은 읽기 전용을 풀어 둔다. 오래된 백업(읽기 전용 파일이 든 예전 백업 포함)은 10개만 남기고 지운다
    [Fact]
    public void BackupsAreWritableAndOldOnesArePruned()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            string pluginFile = Path.Combine(plugin, "FFXIV_ACT_Plugin.dll");
            File.SetAttributes(pluginFile, FileAttributes.ReadOnly);
            // 예전 백업 10개 (가장 오래된 것에 읽기 전용 파일)
            var old = new List<string>();
            for (int i = 1; i <= 10; i++)
            {
                string d = tmp.Dir($"project/backups/old-{i:00}");
                new BackupManifest(DateTime.Now.AddDays(-i), "3.0.3.0", [new(plugin, "0", [], [])], null).Save(d);
                string f = tmp.File($"project/backups/old-{i:00}/0/FFXIV_ACT_Plugin.dll", "older");
                File.SetAttributes(f, FileAttributes.ReadOnly);
                old.Add(d);
            }

            var result = Install.Run(ws, Install.Plan(ws, plugin, "-"));

            string copy = Path.Combine(result.BackupDir, "0", "FFXIV_ACT_Plugin.dll");
            Assert.Equal("old official", File.ReadAllText(copy));
            Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
            Assert.False(Directory.Exists(old[^1]));                           // 가장 오래된 것 (읽기 전용 파일이 있어도)
            Assert.Equal(10, Directory.GetDirectories(ws.BackupsDir).Length);

            foreach (string f in Directory.GetFiles(ws.BackupsDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);                  // 임시 폴더 정리용
        }
    }

    [Fact]
    public void LockedFileLeavesNoTempFile()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = Setup();
        using (tmp)
        {
            string zzz = tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/zzz.dll", "old zzz");
            string before = Snapshot(plugin);
            using (new FileStream(zzz, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<ToolException>(() => Install.Run(ws, Install.Plan(ws, plugin, "-")));
            Assert.Equal(before, Snapshot(plugin));
        }
    }
}
