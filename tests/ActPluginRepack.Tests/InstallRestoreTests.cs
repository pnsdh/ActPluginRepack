namespace ActPluginRepack.Tests;

// 다시 설치·되돌리기가 도중에 막혔을 때도 원래 파일로 돌아갈 길이 남아야 한다
[Collection("elevation")] // 가짜 게임 프로세스를 띄우는 시험과 겹치지 않게
public class InstallRestoreTests
{
    private sealed record Env(TempDir Tmp, Workspace Ws, string Plugin, string Resources) : IDisposable
    {
        public string PluginFile => Path.Combine(Plugin, "FFXIV_ACT_Plugin.dll");
        public string Jsonc => Path.Combine(Resources, global::Install.OverlayOpcodes);
        public InstallResult Install(bool overlay = false) =>
            global::Install.Run(Ws, global::Install.Plan(Ws, Plugin, overlay ? Resources : "-"));
        public void Dispose() => Tmp.Dispose();
    }

    // output\ 에 패치본(+ OverlayPlugin opcodes), ACT 에 옛 공식본과 OverlayPlugin
    private static Env Setup()
    {
        var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/output/FFXIV_ACT_Plugin.dll", "our patch");
        tmp.File("project/output/deucalion-1.5.1.distrib.dll", "new deucalion");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.OverlayOpcodesPath)!);
        File.WriteAllText(ws.OverlayOpcodesPath, "our opcodes");
        string plugin = tmp.Dir("act/Plugins/FFXIV_ACT_Plugin_KR");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", "old official");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/deucalion-1.5.0.dll", "old deucalion");
        tmp.File("act/Plugins/OverlayPlugin/OverlayPlugin.dll", "overlay");
        tmp.File("act/Plugins/OverlayPlugin/resources/opcodes.jsonc", "official opcodes");
        return new Env(tmp, ws, plugin, Path.Combine(tmp.Path, "act", "Plugins", "OverlayPlugin", "resources"));
    }

    private static void AssertOriginal(Env e)
    {
        Assert.Equal("old official", File.ReadAllText(e.PluginFile));
        Assert.Equal("old deucalion", File.ReadAllText(Path.Combine(e.Plugin, "deucalion-1.5.0.dll")));
        Assert.False(File.Exists(Path.Combine(e.Plugin, "deucalion-1.5.1.distrib.dll")));
        Assert.False(File.Exists(Path.Combine(e.Plugin, "deucalion-1.5.1.dll")));
    }

    // 플러그인은 시작할 때 distrib 의 이름을 바꾸고, 해루봇은 opcodes.jsonc 를 덮어쓴다 — 같은 패치본을 다시 설치해도
    // 새 백업(패치본이 "원래 파일"인)을 만들지 않고, 되돌리기 한 번으로 원래 파일로 돌아간다
    [Fact]
    public void ReinstallAfterActStartedKeepsTheOriginalBackup()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        string first = e.Install(overlay: true).BackupDir;
        File.Move(Path.Combine(e.Plugin, "deucalion-1.5.1.distrib.dll"), Path.Combine(e.Plugin, "deucalion-1.5.1.dll"));

        Assert.Equal("", e.Install().BackupDir);                // 플러그인만: 바꿀 것 없음
        File.WriteAllText(e.Jsonc, "official opcodes");               // 해루봇이 OverlayPlugin 배포본으로 덮어씀
        var again = e.Install(overlay: true);                   // opcodes.jsonc 만 다시 넣음
        Assert.Equal(first, again.BackupDir);
        Assert.Equal("our opcodes", File.ReadAllText(e.Jsonc));
        Assert.Single(Directory.GetDirectories(e.Ws.BackupsDir));

        Install.Restore(e.Ws);
        AssertOriginal(e);
        Assert.Equal("official opcodes", File.ReadAllText(e.Jsonc));
    }

    // 처음 설치 때 없던 대상(OverlayPlugin)만 나중에 생기면, 새 백업 대신 첫 백업에 보탠다 — 되돌리기 한 번으로 둘 다 원래대로
    [Fact]
    public void LaterOverlayTargetIsAddedToTheFirstBackup()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        string first = e.Install().BackupDir;           // OverlayPlugin 없이
        Assert.Equal(first, e.Install(overlay: true).BackupDir);
        Assert.Single(Directory.GetDirectories(e.Ws.BackupsDir));
        Assert.Equal("our opcodes", File.ReadAllText(e.Jsonc));

        Install.Restore(e.Ws);
        AssertOriginal(e);
        Assert.Equal("official opcodes", File.ReadAllText(e.Jsonc));
    }

    // 그 사이 OverlayPlugin 이 업데이트되어 opcodes.jsonc 가 바뀌었으면, 되돌릴 원래 파일도 지금 것으로 바꿔 둔다
    [Fact]
    public void ReinstallKeepsANewerOverlayFileAsTheOriginal()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        e.Install(overlay: true);
        File.WriteAllText(e.Jsonc, "updated official opcodes");
        e.Install(overlay: true);
        Assert.Equal("our opcodes", File.ReadAllText(e.Jsonc));

        Install.Restore(e.Ws);
        AssertOriginal(e);
        Assert.Equal("updated official opcodes", File.ReadAllText(e.Jsonc));
    }

    // 백업이 가리키는 폴더가 모두 없어졌으면 되돌리기를 내놓지 않는다
    [Fact]
    public void BackupWhoseFoldersAreAllGoneIsNotOffered()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        e.Install();
        Directory.Delete(e.Plugin, recursive: true);
        Assert.Null(Install.RestorableBackup(e.Ws));
        Assert.Contains("모두 없어져", Install.Restorable(e.Ws).Reason);
    }

    // 되돌리기가 한 파일에서 막혀도(백신이 잡고 있음 등) 플러그인은 이미 원래 것 — 다시 하면 마저 되돌린다
    [Fact]
    public void RestoreCanBeRetriedAfterPartialFailure()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        e.Install();
        string distrib = Path.Combine(e.Plugin, "deucalion-1.5.1.distrib.dll");
        using (new FileStream(distrib, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<ToolException>(() => Install.Restore(e.Ws));
        Assert.Equal("old official", File.ReadAllText(e.PluginFile));

        Assert.NotNull(Install.RestorableBackup(e.Ws));
        Install.Restore(e.Ws);
        AssertOriginal(e);
    }

    // 설치가 옛 deucalion 을 지운 뒤 플러그인을 바꾸기 전에 멈췄어도(프로세스 종료 등) 되돌릴 수 있다
    [Fact]
    public void RestoreWorksAfterInstallStoppedBeforeReplacingThePlugin()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        e.Install();
        File.WriteAllText(e.PluginFile, "old official");
        File.Delete(Path.Combine(e.Plugin, "deucalion-1.5.1.distrib.dll"));

        Install.Restore(e.Ws);
        AssertOriginal(e);
    }

    // 원래 파일의 사본이 없어졌으면(백신 격리 등) 아무것도 바꾸기 전에 멈춘다
    [Fact]
    public void RestoreRefusesWhenABackupCopyIsMissing()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        string backup = e.Install().BackupDir;
        File.Delete(Path.Combine(backup, "0", "deucalion-1.5.0.dll"));

        var ex = Assert.Throws<ToolException>(() => Install.Restore(e.Ws));
        Assert.Contains("백업해 둔 원래 파일이 없습니다", ex.Message);
        Assert.Equal("our patch", File.ReadAllText(e.PluginFile));
        Assert.Null(Install.RestorableBackup(e.Ws));
        Assert.Contains("백업해 둔 원래 파일이 없습니다", Install.Restorable(e.Ws).Reason);
    }

    // 설치한 뒤 OverlayPlugin 을 지웠어도 FFXIV 플러그인은 되돌린다
    [Fact]
    public void RestoreSkipsAFolderThatWasRemoved()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        e.Install(overlay: true);
        Directory.Delete(Path.GetDirectoryName(e.Resources)!, recursive: true);

        Install.Restore(e.Ws);
        AssertOriginal(e);
    }

    // 백업 정리는 이미 되돌린 것부터 — 설치·되돌리기를 여러 번 해도 공식 플러그인이 든 첫 백업은 남는다
    [Fact]
    public void PruningDropsRestoredBackupsFirst()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        string first = e.Install().BackupDir;
        for (int i = 0; i < 11; i++)
        {
            File.WriteAllText(Path.Combine(e.Ws.OutputDir, "FFXIV_ACT_Plugin.dll"), $"patch {i}");
            e.Install();
            Install.Restore(e.Ws); // 바로 앞 패치본(our patch)으로
        }
        Assert.Equal(first, Install.LatestBackup(e.Ws));
        Assert.True(Directory.GetDirectories(e.Ws.BackupsDir).Length <= 10);
        Install.Restore(e.Ws);
        AssertOriginal(e);
    }
}
