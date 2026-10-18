namespace ActPluginRepack.Tests;

// ACTREPACK_ACT_CONFIG 는 프로세스 전역이므로 다른 시험과 동시에 돌리지 않는다
[CollectionDefinition("act-config", DisableParallelization = true)]
public sealed class ActConfigCollection;

// status 의 "공식 최신본으로 바꿀 수 있음" 판단 (가짜 ACT 설정 + 버전을 정한 가짜 플러그인)
[Collection("act-config")]
public class StatusOfficialUpdateTests
{
    private const string GameVersion = "2026.10.06.0000.0000";
    private const string Official = "3.0.3.3";

    private sealed class Setup : IDisposable
    {
        private readonly TempDir _tmp = new();
        private readonly string? _oldEnv = Environment.GetEnvironmentVariable("ACTREPACK_ACT_CONFIG");
        public Workspace Ws { get; }
        public string ActPlugin { get; }

        // 공식 최신본(input\plugin)과 그 기준의 prepare 기록, ACT 에는 actDll (null 이면 공식 최신본과 같은 파일)
        public Setup(byte[]? actDll, bool? patchNeeded = false, string preparedPlugin = Official, string preparedGame = GameVersion)
        {
            Ws = new Workspace(_tmp.Dir("project"));
            byte[] official = FakeAssembly.WithVersion("FFXIV_ACT_Plugin", Official, "official");
            _tmp.File("project/input/plugin/FFXIV_ACT_Plugin.dll", official);
            _tmp.File("game/game/ffxiv_dx11.exe", "x");
            _tmp.File("game/game/ffxivgame.ver", GameVersion);
            _tmp.File("project/config.json", $$"""{ "ida": false, "clients": { "korean": "{{Path.Combine(_tmp.Path, "game").Replace("\\", "/")}}" } }""");
            new MaterialState(DateTime.Now, preparedPlugin, new() { ["korean"] = preparedGame }, [], true, patchNeeded).Save(Ws);

            ActPlugin = _tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", actDll ?? official);
            string config = _tmp.File("act/Advanced Combat Tracker.config.xml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Config>
                  <ActPlugins>
                    <Plugin Path="{Path.Combine(_tmp.Path, "act", "OverlayPlugin.dll")}" Enabled="True" />
                    <Plugin Path="{ActPlugin}" Enabled="True" />
                  </ActPlugins>
                </Config>
                """);
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", config);
        }

        public string File(string relative, byte[] content) => _tmp.File(relative, content);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", _oldEnv);
            _tmp.Dispose();
        }
    }

    private static byte[] Plugin(string version, string marker) => FakeAssembly.WithVersion("FFXIV_ACT_Plugin", version, marker);

    [Fact]
    public void SameVersionButDifferentBytesCanBeReplaced()
    {
        // 패치본은 버전 번호를 바꾸지 않는다 -> 버전이 같아도 내용이 다르면 공식 최신본으로 바꿀 수 있다
        using var s = new Setup(Plugin(Official, "patched"));
        var report = Status.Collect(s.Ws);
        Assert.Null(report.ActError);
        Assert.Equal(Official, report.BasePluginVersion);
        Assert.Empty(report.StaleReasons);
        Assert.True(report.OfficialUpdate);
        Assert.Contains(report.Advice, a => a.Contains("install --official"));
    }

    [Fact]
    public void OlderOfficialInActCanBeReplaced()
    {
        using var s = new Setup(Plugin("3.0.3.1", "old official"));
        Assert.True(Status.Collect(s.Ws).OfficialUpdate);
    }

    [Fact]
    public void OurPatchIsNamedInAdvice()
    {
        byte[] patched = Plugin(Official, "patched");
        using var s = new Setup(patched);
        s.File("project/output/FFXIV_ACT_Plugin.dll", patched);
        var report = Status.Collect(s.Ws);
        Assert.True(report.InstalledIsOurs);
        Assert.True(report.OfficialUpdate);
        Assert.Contains(report.Advice, a => a.Contains("이 프로그램의 패치본"));
    }

    [Fact]
    public void NewerPluginInActIsLeftAlone()
    {
        using var s = new Setup(Plugin("3.0.3.4", "newer"));
        var report = Status.Collect(s.Ws);
        Assert.False(report.OfficialUpdate);
        Assert.DoesNotContain(report.Advice, a => a.Contains("install --official"));
    }

    [Fact]
    public void IdenticalPluginNeedsNothing()
    {
        using var s = new Setup(null);
        Assert.False(Status.Collect(s.Ws).OfficialUpdate);
    }

    [Fact]
    public void StalePrepareIsNotTrusted()
    {
        // prepare 이후 공식 플러그인이 바뀜 / 게임이 패치됨 -> 그 판단(PatchNeeded=false)을 믿지 않는다
        using (var s = new Setup(Plugin(Official, "patched"), preparedPlugin: "3.0.3.2"))
        {
            var report = Status.Collect(s.Ws);
            Assert.NotEmpty(report.StaleReasons);
            Assert.False(report.OfficialUpdate);
            Assert.Contains(report.Advice, a => a.Contains("prepare 를 다시"));
        }
        using (var s = new Setup(Plugin(Official, "patched"), preparedGame: "2026.09.15.0000.0000"))
            Assert.False(Status.Collect(s.Ws).OfficialUpdate);
    }

    [Fact]
    public void PatchNeededOrUnknownIsNotAnOfficialUpdate()
    {
        using (var s = new Setup(Plugin("3.0.3.1", "old"), patchNeeded: true))
            Assert.False(Status.Collect(s.Ws).OfficialUpdate);
        using (var s = new Setup(Plugin("3.0.3.1", "old"), patchNeeded: null))
            Assert.False(Status.Collect(s.Ws).OfficialUpdate);
    }

    // 지금 어디까지 왔는지 — CLI 의 다음에 할 일과 GUI 첫 화면이 같은 판단을 쓴다
    [Fact]
    public void StageFollowsPrepareBuildAndInstall()
    {
        byte[] patched = Plugin(Official, "patched");
        using var s = new Setup(Plugin(Official, "official in act"), patchNeeded: true);
        Assert.Equal(StatusStage.NotBuilt, Status.Collect(s.Ws).Stage);

        // 지난 공식 플러그인으로 만든 결과는 지금 것이 아니다
        string output = Directory.CreateDirectory(s.Ws.OutputDir).FullName;
        s.File("project/output/FFXIV_ACT_Plugin.dll", patched);
        new BuildInfo(DateTime.Now.AddMinutes(1), "3.0.3.2", [], true).Save(output);
        Assert.Equal(StatusStage.NotBuilt, Status.Collect(s.Ws).Stage);

        new BuildInfo(DateTime.Now.AddMinutes(1), Official, [], true).Save(output);
        Assert.Equal(StatusStage.NotInstalled, Status.Collect(s.Ws).Stage);

        File.WriteAllBytes(s.ActPlugin, patched);
        var report = Status.Collect(s.Ws);
        Assert.Equal(StatusStage.Patched, report.Stage);
        Assert.True(report.PatchInstalled); // output\ 의 것과 같으면 백업 기록이 없어도
    }

    // prepare 가 후크가 깨진 채 / opcode 가 빠진 채 끝났으면 build 를 안내하지 않는다 (prepare 의 결론을 그대로)
    [Fact]
    public void UnfinishedPrepareIsNotReadyToBuild()
    {
        using var s = new Setup(Plugin(Official, "official in act"), patchNeeded: true);
        foreach (var outcome in new[] { PrepareOutcome.HooksBroken, PrepareOutcome.OpcodesMissing })
        {
            (MaterialState.Load(s.Ws)! with { Outcome = outcome }).Save(s.Ws);
            var report = Status.Collect(s.Ws);
            Assert.Equal(StatusStage.NotReady, report.Stage);
            Assert.DoesNotContain(report.Advice, a => a.Contains("build 를 실행"));
        }
        (MaterialState.Load(s.Ws)! with { Outcome = PrepareOutcome.PatchNeeded }).Save(s.Ws);
        Assert.Equal(StatusStage.NotBuilt, Status.Collect(s.Ws).Stage);
    }

    // ACT 에 받아 둔 공식본보다 새 플러그인(해루봇의 한국 임시본 등)이 있으면 설치를 안내하지 않는다 — install 이 덮어쓰지 않으므로
    [Fact]
    public void NewerPluginInActIsItsOwnStage()
    {
        foreach (bool? patchNeeded in new bool?[] { true, false })
        {
            using var s = new Setup(Plugin("3.0.3.9", "haerubot kr"), patchNeeded: patchNeeded);
            var report = Status.Collect(s.Ws);
            Assert.Equal(StatusStage.NewerInstalled, report.Stage);
            Assert.False(report.OfficialUpdate);
            Assert.DoesNotContain(report.Advice, a => a.Contains("install 로"));
        }
    }

    [Fact]
    public void OfficialMatchStageAndUndecided()
    {
        using (var s = new Setup(null))
            Assert.Equal(StatusStage.OfficialMatches, Status.Collect(s.Ws).Stage);
        using (var s = new Setup(null, patchNeeded: null))
            Assert.Equal(StatusStage.Undecided, Status.Collect(s.Ws).Stage);
    }

    [Fact]
    public void MissingActConfigIsReportedNotThrown()
    {
        using var s = new Setup(Plugin("3.0.3.1", "old"));
        Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", Path.Combine(s.Ws.Root, "no-such.xml"));
        var report = Status.Collect(s.Ws);
        Assert.Null(report.Act);
        Assert.NotNull(report.ActError);
        Assert.False(report.OfficialUpdate);
    }
}
