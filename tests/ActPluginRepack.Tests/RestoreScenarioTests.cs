namespace ActPluginRepack.Tests;

// 설치·되돌리기를 여러 번 섞고 그 사이 해루봇이 플러그인 폴더를 바꿨을 때, restore 한 번이 무엇을 되돌리고 무엇을 두는지,
// 그 뒤 화면의 [공식 플러그인으로 되돌리기](PatchInstalled)가 보이는지
// (InstallRollbackTests 와 같은 묶음: 가짜 게임 프로세스를 띄우는 시험과 겹치지 않게)
[Collection("elevation")]
public class RestoreScenarioTests
{
    private const string PluginFile = "FFXIV_ACT_Plugin.dll";

    // 원래 공식본 O1, 패치본 P1~P3 (모두 같은 버전, 내용만 다름), 새 공식본 O2
    private static readonly byte[] O1 = Dll("3.0.3.0", "o1"), P1 = Dll("3.0.3.0", "p1"), P2 = Dll("3.0.3.0", "p2"),
                                   P3 = Dll("3.0.3.0", "p3"), O2 = Dll("3.0.3.1", "o2");

    private static byte[] Dll(string version, string marker) => FakeAssembly.WithVersion("FFXIV_ACT_Plugin", version, marker);

    // ACT 의 언어 폴더 하나: 원래 공식본 O1 과 deucalion 1.5.0, 상관없는 파일
    private static string LangDir(TempDir tmp, string relative)
    {
        string dir = tmp.Dir(relative);
        File.WriteAllBytes(Path.Combine(dir, PluginFile), O1);
        File.WriteAllText(Path.Combine(dir, "deucalion-1.5.0.dll"), "deucalion-1.5.0.dll");
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "unrelated");
        return dir;
    }

    // 설치할 파일 (output\ 의 패치본이나 input\plugin 의 공식본): 플러그인과 deucalion 하나 (내용은 그 이름)
    private static void Put(string dir, byte[] plugin, string deucalion)
    {
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(dir, "deucalion*.dll")) File.Delete(f);
        File.WriteAllBytes(Path.Combine(dir, PluginFile), plugin);
        File.WriteAllText(Path.Combine(dir, deucalion), deucalion);
    }

    private static void Patch(Workspace ws, byte[] plugin, string deucalion = "deucalion-1.5.1.distrib.dll") =>
        Put(ws.OutputDir, plugin, deucalion);

    private static InstallResult InstallPatch(Workspace ws, IReadOnlyList<string> dirs) =>
        Install.Run(ws, Install.Plan(ws, dirs, "-"));

    private static InstallResult InstallOfficial(Workspace ws, IReadOnlyList<string> dirs)
    {
        Put(ws.PluginDir, O2, "deucalion-1.5.2.distrib.dll");
        return Install.Run(ws, Install.Plan(ws, dirs, official: true));
    }

    // 화면이 보는 ACT 의 언어 폴더들 (플러그인 파일이 없어진 폴더도)
    private static bool PatchInstalled(Workspace ws, params string[] dirs) =>
        Install.PatchInstalled(ws, new ActTarget(Path.Combine(dirs[0], PluginFile), Enabled: true) { Others = dirs[1..] });

    // 해루봇의 플러그인 업데이트: 폴더를 통째로 지우고 공식본을 다시 받음
    private static void HaerubotUpdate(string dir, byte[] plugin, string deucalion)
    {
        Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, PluginFile), plugin);
        File.WriteAllText(Path.Combine(dir, deucalion), deucalion);
    }

    // 처음 설치 전 그대로 (O1, deucalion 1.5.0 만, 상관없는 파일도 남음)
    private static void AssertOriginal(string dir)
    {
        Assert.Equal(O1, File.ReadAllBytes(Path.Combine(dir, PluginFile)));
        Assert.Equal("deucalion-1.5.0.dll", Path.GetFileName(Assert.Single(Directory.GetFiles(dir, "deucalion*"))));
        Assert.Equal("deucalion-1.5.0.dll", File.ReadAllText(Path.Combine(dir, "deucalion-1.5.0.dll")));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(dir, "keep.txt")));
    }

    // 폴더 안 파일 이름과 내용
    private static string Snapshot(string dir) => string.Join("|",
        Directory.GetFiles(dir).Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{Path.GetFileName(f)}={Convert.ToBase64String(File.ReadAllBytes(f))}"));

    // 1. 패치본 하나 -> 되돌리면 원래 공식본, 버튼 없음
    [Fact]
    public void SinglePatchGoesBackToTheOriginal()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR");
        Assert.False(PatchInstalled(ws, kr));
        Patch(ws, P1);
        string b1 = InstallPatch(ws, [kr]).BackupDir;
        Assert.Equal(b1, Install.LatestBackup(ws));
        Assert.True(PatchInstalled(ws, kr));

        var r = Install.Restore(ws);
        Assert.Equal(b1, r.BackupDir);
        Assert.Empty(r.Skipped);
        AssertOriginal(kr);
        Assert.Null(Install.LatestBackup(ws));
        Assert.False(PatchInstalled(ws, kr));
    }

    // 2. 패치 위에 패치(deucalion 도 바뀜, ACT 를 켜 distrib 이름이 바뀜) -> 한 번에 하나씩: 먼저 P1(이름이 바뀐 deucalion 도
    //    지움), 한 번 더 하면 원래 공식본. 사이에는 아직 패치본이라 버튼이 남는다
    [Fact]
    public void TwoPatchesAreUndoneOneAtATimeAfterDeucalionWasRenamed()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR");
        Patch(ws, P1);
        string b1 = InstallPatch(ws, [kr]).BackupDir;
        Patch(ws, P2, "deucalion-1.5.2.distrib.dll");
        string b2 = InstallPatch(ws, [kr]).BackupDir;
        File.Move(Path.Combine(kr, "deucalion-1.5.2.distrib.dll"), Path.Combine(kr, "deucalion-1.5.2.dll")); // 플러그인이 시작하며 이름을 바꿈

        var first = Install.Restore(ws);
        Assert.Equal(b2, first.BackupDir);
        Assert.Equal(P1, File.ReadAllBytes(Path.Combine(kr, PluginFile)));
        Assert.Equal("deucalion-1.5.1.distrib.dll", Path.GetFileName(Assert.Single(Directory.GetFiles(kr, "deucalion*"))));
        Assert.Equal(b1, Install.LatestBackup(ws));
        Assert.True(PatchInstalled(ws, kr));

        var second = Install.Restore(ws);
        Assert.Equal(b1, second.BackupDir);
        AssertOriginal(kr);
        Assert.Null(Install.LatestBackup(ws));
        Assert.False(PatchInstalled(ws, kr));
    }

    // 3. 패치본 위에 공식본 O2 를 설치 -> 패치본이 아니라 버튼 없음. restore 는 그 설치 하나만 되돌려 그 전 패치본으로
    [Fact]
    public void OfficialInstallOverAPatchIsNotAPatch()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR");
        Patch(ws, P1);
        string b1 = InstallPatch(ws, [kr]).BackupDir;
        string b2 = InstallOfficial(ws, [kr]).BackupDir;
        Assert.Equal(O2, File.ReadAllBytes(Path.Combine(kr, PluginFile)));
        Assert.Equal("deucalion-1.5.2.distrib.dll", Path.GetFileName(Assert.Single(Directory.GetFiles(kr, "deucalion*"))));
        Assert.False(PatchInstalled(ws, kr));

        var r = Install.Restore(ws);
        Assert.Equal(b2, r.BackupDir);
        Assert.Equal(P1, File.ReadAllBytes(Path.Combine(kr, PluginFile)));
        Assert.Equal("deucalion-1.5.1.distrib.dll", Path.GetFileName(Assert.Single(Directory.GetFiles(kr, "deucalion*"))));
        Assert.Equal(b1, Install.LatestBackup(ws));
        Assert.True(PatchInstalled(ws, kr));
    }

    // 4. 설치 뒤 해루봇이 (하나뿐인) 언어 폴더를 새 공식본으로 -> 버튼 없음, restore 도 아무것도 바꾸지 않음
    [Fact]
    public void HaerubotUpdateOfTheOnlyFolderRefusesRestore()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR");
        Patch(ws, P1);
        InstallPatch(ws, [kr]);
        HaerubotUpdate(kr, O2, "deucalion-1.5.2.dll");
        string before = Snapshot(kr);

        Assert.False(PatchInstalled(ws, kr));
        var ex = Assert.Throws<ToolException>(() => Install.Restore(ws));
        Assert.Contains("바뀌어", ex.Message);
        Assert.Equal(before, Snapshot(kr));
    }

    // 5. KR·글로벌에 설치한 뒤 해루봇이 글로벌만 새 공식본으로 -> KR 만 되돌리고 글로벌은 그대로, 그 뒤 버튼 없음
    [Fact]
    public void OnlyTheUntouchedLanguageIsRestored()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR"), global = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_Global");
        Patch(ws, P1);
        string b1 = InstallPatch(ws, [kr, global]).BackupDir;
        HaerubotUpdate(global, O2, "deucalion-1.5.2.dll");
        string globalBefore = Snapshot(global);
        Assert.True(PatchInstalled(ws, kr, global));

        var r = Install.Restore(ws);
        Assert.Equal(b1, r.BackupDir);
        Assert.Contains(global, r.Skipped, StringComparer.OrdinalIgnoreCase);
        AssertOriginal(kr);
        Assert.Equal(globalBefore, Snapshot(global));
        Assert.False(PatchInstalled(ws, kr, global));
    }

    // 6. 설치 뒤 해루봇이 같은 원래 공식본을 다시 받음 -> 패치본이 아니라 버튼 없음, restore 해도 바뀌는 것 없음
    [Fact]
    public void ReDownloadedOriginalIsNotAPatch()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR");
        Patch(ws, P1);
        InstallPatch(ws, [kr]);
        HaerubotUpdate(kr, O1, "deucalion-1.5.0.dll");
        string before = Snapshot(kr);

        Assert.False(PatchInstalled(ws, kr));
        Install.Restore(ws);
        Assert.Equal(before, Snapshot(kr));
    }

    // 7. ACT 폴더 둘: X1 -> Y -> X2(X1 위의 패치) 에서 restore 는 가장 최근 X2 만 되돌린다 (Y 는 그대로, 다음 restore 는 Y)
    [Fact]
    public void RestoreUndoesOnlyTheLatestInstallAcrossActFolders()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string x = LangDir(tmp, "actX/Plugins/FFXIV_ACT_Plugin_KR"), y = LangDir(tmp, "actY/Plugins/FFXIV_ACT_Plugin_KR");
        Patch(ws, P1);
        InstallPatch(ws, [x]);
        string by = InstallPatch(ws, [y]).BackupDir;
        Patch(ws, P2);
        string bx2 = InstallPatch(ws, [x]).BackupDir;
        string yBefore = Snapshot(y);

        var r = Install.Restore(ws);
        Assert.Equal(bx2, r.BackupDir);
        Assert.Equal(P1, File.ReadAllBytes(Path.Combine(x, PluginFile)));
        Assert.Equal(yBefore, Snapshot(y));
        Assert.Equal(by, Install.LatestBackup(ws));
        Assert.True(PatchInstalled(ws, x));
    }

    // 8. 언어 폴더 둘 중 하나를 설치 뒤에 폴더째 지움 -> 남은 폴더만 되돌린다
    [Fact]
    public void RemovedLanguageFolderIsSkipped()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR"), global = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_Global");
        Patch(ws, P1);
        string b1 = InstallPatch(ws, [kr, global]).BackupDir;
        Directory.Delete(global, recursive: true);
        Assert.True(PatchInstalled(ws, kr, global));

        var r = Install.Restore(ws);
        Assert.Equal(b1, r.BackupDir);
        Assert.Contains(global, r.Skipped, StringComparer.OrdinalIgnoreCase);
        AssertOriginal(kr);
        Assert.False(Directory.Exists(global)); // 지운 폴더를 다시 만들지 않는다
        Assert.False(PatchInstalled(ws, kr, global));
    }

    // 9. ACT 를 켠 뒤(distrib 이름이 바뀜) 같은 패치본을 다시 설치하면 아무것도 하지 않고, 글로벌 폴더까지 더해 설치하면
    // 글로벌만 새 백업에 — restore 한 번에 하나씩, 두 번이면 두 폴더 모두 처음 설치 전으로
    [Fact]
    public void ReinstallingTheSamePatchBacksUpOnlyTheNewFolder()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR"), global = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_Global");
        Patch(ws, P1);
        string b1 = InstallPatch(ws, [kr]).BackupDir;
        File.Move(Path.Combine(kr, "deucalion-1.5.1.distrib.dll"), Path.Combine(kr, "deucalion-1.5.1.dll"));

        Assert.Equal("", InstallPatch(ws, [kr]).BackupDir);
        string b2 = InstallPatch(ws, [kr, global]).BackupDir;
        Assert.NotEqual(b1, b2);
        Assert.Equal(P1, File.ReadAllBytes(Path.Combine(global, PluginFile)));

        Assert.Equal(b2, Install.Restore(ws).BackupDir);
        AssertOriginal(global);
        Assert.True(PatchInstalled(ws, kr, global));
        Assert.Equal(b1, Install.Restore(ws).BackupDir);
        AssertOriginal(kr);
        Assert.Null(Install.LatestBackup(ws));
        Assert.False(PatchInstalled(ws, kr, global));
    }

    // 10. P1 -> P2 -> 되돌림(P1) -> P3 설치 -> 되돌리면 P3 설치 전(P1)으로 (되돌린 P2 설치는 건너뜀), 한 번 더 하면 원래 공식본
    [Fact]
    public void InstallRightAfterRestoreIsUndoneToTheRestoredPatch()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR");
        Patch(ws, P1);
        string b1 = InstallPatch(ws, [kr]).BackupDir;
        Patch(ws, P2);
        InstallPatch(ws, [kr]);
        Install.Restore(ws);
        Patch(ws, P3);
        string b3 = InstallPatch(ws, [kr]).BackupDir;

        var r = Install.Restore(ws);
        Assert.Equal(b3, r.BackupDir);
        Assert.Equal(P1, File.ReadAllBytes(Path.Combine(kr, PluginFile)));
        Assert.Equal(b1, Install.LatestBackup(ws));

        Assert.Equal(b1, Install.Restore(ws).BackupDir);
        AssertOriginal(kr);
        Assert.Null(Install.LatestBackup(ws));
    }

    // 11. OverlayPlugin opcodes.jsonc 도 설치한 뒤 opcodes.jsonc 가 다른 내용으로 바뀜 -> 플러그인만 되돌리고
    // opcodes.jsonc 는 그대로 두며 결과(Skipped)에 남긴다
    [Fact]
    public void ChangedOverlayOpcodesAreKeptAndRecorded()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        string kr = LangDir(tmp, "act/Plugins/FFXIV_ACT_Plugin_KR");
        string overlay = tmp.Dir("act/Plugins/OverlayPlugin");
        File.WriteAllText(Path.Combine(overlay, "OverlayPlugin.dll"), "overlay");
        string resources = tmp.Dir("act/Plugins/OverlayPlugin/resources");
        string jsonc = Path.Combine(resources, Install.OverlayOpcodes);
        File.WriteAllText(jsonc, "official opcodes");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.OverlayOpcodesPath)!);
        File.WriteAllText(ws.OverlayOpcodesPath, "our opcodes");
        Patch(ws, P1);

        var plan = Install.Plan(ws, [kr], resources);
        Assert.Equal(resources, plan.OverlayResources, StringComparer.OrdinalIgnoreCase);
        string b1 = Install.Run(ws, plan).BackupDir;
        Assert.Equal("our opcodes", File.ReadAllText(jsonc));
        File.WriteAllText(jsonc, "newer opcodes"); // OverlayPlugin 업데이트 등

        var r = Install.Restore(ws);
        Assert.Equal(b1, r.BackupDir);
        AssertOriginal(kr);
        Assert.Equal("newer opcodes", File.ReadAllText(jsonc));
        Assert.Contains(jsonc, r.Skipped, StringComparer.OrdinalIgnoreCase);
        Assert.Null(Install.LatestBackup(ws));
    }
}
