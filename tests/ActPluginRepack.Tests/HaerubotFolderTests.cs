namespace ActPluginRepack.Tests;

// 해루봇은 FFXIV 플러그인을 언어마다 Plugins\FFXIV_ACT_Plugin_KR / _Global / _CN 에 두고, ACT 설정에는 마지막으로 실행한
// 언어의 폴더만 적는다. KR / Global(같은 공식 플러그인)은 함께, _CN(중국판용으로 고친 다른 플러그인)은 빼고 설치한다.
[Collection("act-config")]
public class HaerubotTargetTests
{
    private sealed class Act : IDisposable
    {
        private readonly string? _oldEnv = Environment.GetEnvironmentVariable("ACTREPACK_ACT_CONFIG");
        public TempDir Tmp { get; } = new();
        public string Plugins => Path.Combine(Tmp.Path, "act", "Plugins");

        // dirs: 플러그인을 넣을 언어 폴더들, registered: ACT 설정에 적힌 폴더
        public Act(string registered, params string[] dirs)
        {
            foreach (string d in dirs) Tmp.File($"act/Plugins/{d}/FFXIV_ACT_Plugin.dll", d);
            string config = Tmp.File("act/Advanced Combat Tracker.config.xml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Config><ActPlugins><Plugin Path="{Path.Combine(Plugins, registered, "FFXIV_ACT_Plugin.dll")}" Enabled="True" /></ActPlugins></Config>
                """);
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", config);
        }

        public string Dir(string name) => Path.Combine(Plugins, name);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", _oldEnv);
            Tmp.Dispose();
        }
    }

    [Fact]
    public void OtherOfficialLanguageFolderIsIncludedButNotChinese()
    {
        using var a = new Act("FFXIV_ACT_Plugin_KR", "FFXIV_ACT_Plugin_KR", "FFXIV_ACT_Plugin_Global", "FFXIV_ACT_Plugin_CN");
        var t = Install.ResolveTarget(null);
        Assert.Equal([a.Dir("FFXIV_ACT_Plugin_KR"), a.Dir("FFXIV_ACT_Plugin_Global")], t.PluginDirs);
    }

    [Fact]
    public void ChineseRegisteredFallsBackToOfficialFolders()
    {
        using var a = new Act("FFXIV_ACT_Plugin_CN", "FFXIV_ACT_Plugin_Global", "FFXIV_ACT_Plugin_CN");
        Assert.Equal([a.Dir("FFXIV_ACT_Plugin_Global")], Install.ResolveTarget(null).PluginDirs);
    }

    [Fact]
    public void ChineseOnlyIsRefused()
    {
        using var a = new Act("FFXIV_ACT_Plugin_CN", "FFXIV_ACT_Plugin_CN");
        var ex = Assert.Throws<ActTargetException>(() => Install.ResolveTarget(null));
        Assert.Equal(ActProblem.ChineseOnly, ex.Problem);
        // 직접 지정해도 (관리자 권한 재실행으로 받은 경로도) 중국판 폴더에는 쓰지 않는다
        Assert.Throws<ToolException>(() => Install.ResolveTarget([a.Dir("FFXIV_ACT_Plugin_CN")]));
    }

    [Fact]
    public void NonHaerubotFolderIsAlone()
    {
        using var a = new Act("FFXIV_ACT_Plugin", "FFXIV_ACT_Plugin", "FFXIV_ACT_Plugin_Global");
        Assert.Equal([a.Dir("FFXIV_ACT_Plugin")], Install.ResolveTarget(null).PluginDirs);
    }

    // 관리자 권한으로 다시 실행할 때 정한 폴더들을 그대로 넘긴다
    [Fact]
    public void ElevatedArgsCarryEveryFolder()
    {
        using var a = new Act("FFXIV_ACT_Plugin_Global", "FFXIV_ACT_Plugin_KR", "FFXIV_ACT_Plugin_Global");
        var ws = new Workspace(a.Tmp.Dir("project"));
        a.Tmp.File("project/output/FFXIV_ACT_Plugin.dll", "patch");
        var args = Install.Plan(ws, overlayResources: "-").ToCliArgs(ws);
        var targets = args.Select((x, i) => (x, i)).Where(p => p.x == "--target").Select(p => args[p.i + 1]).ToList();
        Assert.Equal([a.Dir("FFXIV_ACT_Plugin_Global"), a.Dir("FFXIV_ACT_Plugin_KR")], targets);
        Assert.Equal(targets, Install.ResolveTarget(targets).PluginDirs);
    }

    // 한 언어 폴더에만 패치본이 있으면 설치가 끝난 것이 아니다
    [Fact]
    public void StatusNeedsEveryFolderPatched()
    {
        using var a = new Act("FFXIV_ACT_Plugin_KR", "FFXIV_ACT_Plugin_KR", "FFXIV_ACT_Plugin_Global");
        var ws = new Workspace(a.Tmp.Dir("project"));
        a.Tmp.File("project/output/FFXIV_ACT_Plugin.dll", "patch");
        File.WriteAllText(Path.Combine(a.Dir("FFXIV_ACT_Plugin_KR"), "FFXIV_ACT_Plugin.dll"), "patch");
        Assert.False(Status.Collect(ws).InstalledIsOurs);
        File.WriteAllText(Path.Combine(a.Dir("FFXIV_ACT_Plugin_Global"), "FFXIV_ACT_Plugin.dll"), "patch");
        Assert.True(Status.Collect(ws).InstalledIsOurs);
    }
}

[Collection("elevation")] // 가짜 게임 프로세스를 띄우는 시험과 겹치지 않게
public class HaerubotInstallTests
{
    private sealed record Env(TempDir Tmp, Workspace Ws, string Kr, string Global) : IDisposable
    {
        public InstallResult Install(params string[] dirs) => global::Install.Run(Ws, global::Install.Plan(Ws, dirs, "-"));
        public static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));
        public void Dispose() => Tmp.Dispose();
    }

    private static Env Setup()
    {
        var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/output/FFXIV_ACT_Plugin.dll", "our patch");
        tmp.File("project/output/deucalion-1.5.1.distrib.dll", "new deucalion");
        foreach (string lang in new[] { "KR", "Global" })
        {
            tmp.File($"act/Plugins/FFXIV_ACT_Plugin_{lang}/FFXIV_ACT_Plugin.dll", $"old official {lang}");
            tmp.File($"act/Plugins/FFXIV_ACT_Plugin_{lang}/deucalion-1.5.0.dll", $"old deucalion {lang}");
        }
        string Dir(string lang) => Path.Combine(tmp.Path, "act", "Plugins", $"FFXIV_ACT_Plugin_{lang}");
        return new Env(tmp, ws, Dir("KR"), Dir("Global"));
    }

    private static void AssertOriginal(string dir, string lang)
    {
        Assert.Equal($"old official {lang}", Env.Read(dir, "FFXIV_ACT_Plugin.dll"));
        Assert.Equal($"old deucalion {lang}", Env.Read(dir, "deucalion-1.5.0.dll"));
        Assert.False(File.Exists(Path.Combine(dir, "deucalion-1.5.1.distrib.dll")));
    }

    private static void AssertPatched(string dir)
    {
        Assert.Equal("our patch", Env.Read(dir, "FFXIV_ACT_Plugin.dll"));
        Assert.Equal("new deucalion", Env.Read(dir, "deucalion-1.5.1.distrib.dll"));
        Assert.False(File.Exists(Path.Combine(dir, "deucalion-1.5.0.dll")));
    }

    [Fact]
    public void BothFoldersAreInstalledAndRestoredTogether()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        e.Install(e.Kr, e.Global);
        AssertPatched(e.Kr);
        AssertPatched(e.Global);
        Assert.Single(Directory.GetDirectories(e.Ws.BackupsDir));

        Install.Restore(e.Ws);
        AssertOriginal(e.Kr, "KR");
        AssertOriginal(e.Global, "Global");
    }

    // 예전처럼 한 폴더에만 설치한 뒤 두 폴더로 다시 설치하면, 다른 폴더를 첫 백업에 보탠다 — 되돌리기 한 번으로 둘 다
    [Fact]
    public void LaterFolderIsAddedToTheFirstBackup()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        string first = e.Install(e.Kr).BackupDir;
        Assert.Equal(first, e.Install(e.Kr, e.Global).BackupDir);
        Assert.Single(Directory.GetDirectories(e.Ws.BackupsDir));
        AssertPatched(e.Global);

        Install.Restore(e.Ws);
        AssertOriginal(e.Kr, "KR");
        AssertOriginal(e.Global, "Global");
    }

    // 설치한 뒤 해루봇에서 한 언어만 업데이트했으면 그 폴더는 두고 나머지만 되돌린다
    [Fact]
    public void RestoreLeavesAFolderUpdatedSinceInstall()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        e.Install(e.Kr, e.Global);
        File.WriteAllText(Path.Combine(e.Global, "FFXIV_ACT_Plugin.dll"), "newer official");
        Assert.NotNull(Install.RestorableBackup(e.Ws));

        Install.Restore(e.Ws);
        AssertOriginal(e.Kr, "KR");
        Assert.Equal("newer official", Env.Read(e.Global, "FFXIV_ACT_Plugin.dll"));

        // 모든 폴더가 바뀌었으면 되돌리지 않는다
        e.Install(e.Kr, e.Global);
        File.WriteAllText(Path.Combine(e.Kr, "FFXIV_ACT_Plugin.dll"), "newest official");
        File.WriteAllText(Path.Combine(e.Global, "FFXIV_ACT_Plugin.dll"), "newest official");
        Assert.Null(Install.RestorableBackup(e.Ws));
    }

    // 다른 언어 폴더에 더 새 플러그인이 있으면 그 폴더는 빼고 설치한다
    [Fact]
    public void NewerPluginInTheOtherFolderIsSkipped()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var e = Setup();
        File.WriteAllBytes(Path.Combine(e.Ws.OutputDir, "FFXIV_ACT_Plugin.dll"), FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3", "patch"));
        byte[] newer = FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.4", "newer");
        File.WriteAllBytes(Path.Combine(e.Global, "FFXIV_ACT_Plugin.dll"), newer);

        var plan = Install.Plan(e.Ws, [e.Kr, e.Global], "-");
        Assert.Equal([e.Kr], plan.Plugin.PluginDirs);
        Install.Run(e.Ws, plan);
        Assert.Equal(newer, File.ReadAllBytes(Path.Combine(e.Global, "FFXIV_ACT_Plugin.dll")));
    }
}
