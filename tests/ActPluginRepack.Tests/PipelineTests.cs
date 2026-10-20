using System.Text.Json.Nodes;

namespace ActPluginRepack.Tests;

public class ConfigTests
{
    [Fact]
    public void BadClientPathIsReportedPerClient()
    {
        using var tmp = new TempDir();
        tmp.File("game/game/ffxiv_dx11.exe", "x");
        tmp.File("game/game/ffxivgame.ver", "2026.10.06.0000.0000");
        tmp.File("config.json", """
            {
              // 주석 허용
              "clients": { "korean": "game", "global": "nope" },
            }
            """);
        var config = Config.Load(tmp.Path);
        var client = Assert.Single(config.Clients);              // 상대 경로는 프로젝트 기준
        Assert.Equal("2026.10.06.0000.0000", client.Version);
        Assert.Contains("global", Assert.Single(config.ClientErrors));
    }

    [Fact]
    public void OptionalToolsDefaultOnAndCanBeTurnedOff()
    {
        using var tmp = new TempDir();
        tmp.File("config.json", "{}");
        var auto = Config.Load(tmp.Path);
        Assert.True(auto.LocalDiff);                               // 기본 켜짐 (Python 이 있을 때만 실제로 돎)
        Assert.True(auto.IdaDetected);                             // "ida" 가 없으면 Program Files 에서 찾는다
        if (auto.Ida is { } found)                                 // 찾았다면 idat 가 있는 Program Files 의 IDA* 폴더
        {
            Assert.StartsWith("IDA", Path.GetFileName(found), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(found, "idat.exe")) || File.Exists(Path.Combine(found, "idat64.exe")));
            Assert.Contains(new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                                .Select(Environment.GetFolderPath).Where(p => p.Length > 0),
                            p => string.Equals(Path.GetDirectoryName(found), p, StringComparison.OrdinalIgnoreCase));
        }

        tmp.File("config.json", """{ "localDiff": false, "ida": false }""");
        var off = Config.Load(tmp.Path);
        Assert.False(off.LocalDiff);
        Assert.Null(off.Ida);
        Assert.False(off.IdaDetected);

        tmp.File("config.json", """{ "ida": "tools/ida" }""");
        var set = Config.Load(tmp.Path);
        Assert.Equal(Path.Combine(tmp.Path, "tools", "ida"), set.Ida);    // 직접 준 경로가 자동 탐색보다 우선 (없는 폴더여도)
        Assert.False(set.IdaDetected);
    }

    // 읽을 수 없는 config.json 은 옆에 보관하고 치운다 (화면에서 새로 설정할 수 있게). 멀쩡하면(주석 포함) 그대로.
    [Fact]
    public void MoveBrokenKeepsUnreadableConfigAside()
    {
        using var tmp = new TempDir();
        Assert.Null(Config.MoveBroken(tmp.Path));                          // 없음

        string path = tmp.File("config.json", "{\n  // 주석\n  \"ida\": false,\n}\n");
        Assert.Null(Config.MoveBroken(tmp.Path));                          // 주석·끝 쉼표는 읽을 수 있음
        Assert.True(File.Exists(path));

        tmp.File("config.json", """{ "clients": { "korean": "C:\Games" } }"""); // \G — JSON 이 깨짐
        string? kept = Config.MoveBroken(tmp.Path);
        Assert.NotNull(kept);
        Assert.False(File.Exists(path));
        Assert.StartsWith("config.json.broken-", Path.GetFileName(kept));
        Assert.Contains(@"C:\Games", File.ReadAllText(kept));             // 내용은 그대로 보관
        Assert.Empty(Config.Load(tmp.Path).Clients);                       // 이제 설정 없음으로 시작
    }

    [Fact]
    public void SetKeepsOtherKeys()
    {
        using var tmp = new TempDir();
        tmp.File("config.json", """{ "oxidizer": true, "clients": {} }""");
        Config.SetClients(tmp.Path, new Dictionary<string, string> { ["korean"] = @"C:\KR" });
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(tmp.Path, "config.json")))!;
        Assert.True(json["oxidizer"]!.GetValue<bool>());
        Assert.Equal(@"C:\KR", json["clients"]!["korean"]!.GetValue<string>());
        Assert.Throws<ToolException>(() => Config.SetClients(tmp.Path, new Dictionary<string, string> { ["mars"] = "x" }));
    }

    [Fact]
    public void InfersRegionFromLauncher()
    {
        using var tmp = new TempDir();
        tmp.File("kr/boot/FFXIV_Boot.exe", "x");
        tmp.File("gl/boot/ffxivboot.exe", "x");
        Assert.Equal("korean", ClientLocator.InferRegion(Path.Combine(tmp.Path, "kr"))?.Name);
        Assert.Equal("global", ClientLocator.InferRegion(Path.Combine(tmp.Path, "gl"))?.Name);
        Assert.Null(ClientLocator.InferRegion(tmp.Path));
        // 중국판은 런처가 글로벌판과 같은 ffxivboot.exe — SDO 로그인 모듈로 먼저 가린다
        tmp.File("cn/boot/ffxivboot.exe", "x");
        tmp.File("cn/sdo/sdologin/sdologinentry64.dll", "x");
        Assert.Equal("simplified-chinese", ClientLocator.InferRegion(Path.Combine(tmp.Path, "cn"))?.Name);
    }

    // 추정이 적어 둔 지역과 "확실히" 다른 때만: 글로벌 런처는 표시가 약해 중국(sdo 없는 설치)·대만판도 글로벌로 보일 수 있다
    [Fact]
    public void RegionContradictionIgnoresWeakGlobalGuess()
    {
        Assert.True(ClientLocator.Contradicts(Region.SimplifiedChinese, Region.Global));   // 예전에 global 로 적힌 중국판
        Assert.True(ClientLocator.Contradicts(Region.Korean, Region.Global));
        Assert.True(ClientLocator.Contradicts(Region.Global, Region.Korean));
        Assert.False(ClientLocator.Contradicts(Region.Global, Region.TraditionalChinese));
        Assert.False(ClientLocator.Contradicts(Region.Global, Region.SimplifiedChinese));
        Assert.False(ClientLocator.Contradicts(Region.Korean, Region.Korean));
    }

    // 기본으로 고르는(쓰는) 클라이언트는 한국·글로벌 — 중국·대만판은 찾아도 사용자가 고를 때만
    [Fact]
    public void OnlyKoreanAndGlobalAreSelectedByDefault()
    {
        Assert.True(ClientLocator.SelectedByDefault(Region.Global));
        Assert.True(ClientLocator.SelectedByDefault(Region.Korean));
        Assert.False(ClientLocator.SelectedByDefault(Region.SimplifiedChinese));
        Assert.False(ClientLocator.SelectedByDefault(Region.TraditionalChinese));
        Assert.False(ClientLocator.SelectedByDefault(null));
    }

    // 설치 기록에 설치 위치가 없으면(중국판) 아이콘 경로 ...\boot\ffxivboot.exe 에서
    [Theory]
    [InlineData(@"C:\Games\FF14\boot\ffxivboot.exe", @"C:\Games\FF14")]
    [InlineData(@"""C:\Games\FF14\boot\ffxivboot.exe"",0", @"C:\Games\FF14")]
    [InlineData(@"C:\Games\FF14\game\ffxiv_dx11.exe", @"C:\Games\FF14\game\ffxiv_dx11.exe")]   // boot 밖이면 그대로 (찾기가 확인)
    [InlineData(@"", null)]
    public void InstallDirFromUninstallIcon(string icon, string? expected) =>
        Assert.Equal(expected, ClientLocator.InstallDirOfIcon(icon));
}

public class MaterialStateTests
{
    [Fact]
    public void ReportsChangesSincePrepare()
    {
        var state = new MaterialState(DateTime.Now, "3.0.3.1", new() { ["korean"] = "2026.09.15.0000.0000" }, [], true, false);
        var client = new GameClient("korean", Region.Find("korean"), @"C:\x\ffxiv_dx11.exe", "2026.10.06.0000.0000");
        var diffs = state.Differences(@"C:\does\not\exist.dll", [client]);
        Assert.Equal(2, diffs.Count); // 플러그인 바뀜(없음) + 게임 패치됨
        Assert.Contains(diffs, d => d.Contains("2026.09.15.0000.0000 -> 2026.10.06.0000.0000"));
    }

    [Fact]
    public void RoundTripsThroughFile()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        new MaterialState(DateTime.Now, "1.2", new() { ["global"] = "v" },
            new() { ["global"] = new MaterialState.OpcodeInfo("src", 0, 26, 2) }, null, null).Save(ws);
        var loaded = MaterialState.Load(ws)!;
        Assert.Equal(2, loaded.Opcodes["global"].Missing);
        Assert.Null(loaded.PatchNeeded);
    }

    [Fact]
    public void OutdatedClientIsNotStaleUntilUpdated()
    {
        // 한국 클라이언트는 최신이 아니라 prepare 에서 뺐다: 그대로면 "바뀜"이 아니고, 업데이트하면 다시 prepare 하라고 알린다
        var state = new MaterialState(DateTime.Now, null, new() { ["global"] = "2026.10.06.0000.0000" }, [], true, true,
                                      Outdated: new() { ["korean"] = "2026.10.02.0000.0000" });
        var global = new GameClient("global", Region.Find("global"), @"C:\g\ffxiv_dx11.exe", "2026.10.06.0000.0000");
        var korean = new GameClient("korean", Region.Find("korean"), @"C:\k\ffxiv_dx11.exe", "2026.10.02.0000.0000");
        Assert.True(state.IsOutdated(korean));
        Assert.False(state.IsOutdated(global));
        Assert.Empty(state.Differences(@"C:\does\not\exist.dll", [global, korean]));

        var updated = korean with { Version = "2026.10.06.0000.0000" };
        Assert.False(state.IsOutdated(updated));
        Assert.Contains(state.Differences(@"C:\does\not\exist.dll", [global, updated]), d => d.Contains("korean"));
    }

    // 공식 패치 서버 exe 로 준비한 재료: 내 게임(공식 패치 서버가 있는 지역)과는 비교하지 않는다 — 대만판처럼 그대로 쓴 클라이언트만
    [Fact]
    public void ServerExeMaterialIgnoresMyGame()
    {
        var state = new MaterialState(DateTime.Now, null,
            new() { ["global"] = "2026.10.16.0000.0000", ["korean"] = "2026.10.06.0000.0000", ["traditional-chinese"] = "2026.09.01.0000.0000" },
            [], true, true, FromServer: true);
        var korean = new GameClient("korean", Region.Korean, @"C:\k\ffxiv_dx11.exe", "2026.09.15.0000.0000"); // 업데이트 안 한 내 게임
        var tc = new GameClient("traditional-chinese", Region.TraditionalChinese, @"C:\t\ffxiv_dx11.exe", "2026.09.01.0000.0000");
        Assert.Empty(state.Differences(@"C:\does\not\exist.dll", [korean, tc]));
        Assert.Contains(state.Differences(@"C:\does\not\exist.dll", [korean, tc with { Version = "2026.10.01.0000.0000" }]),
                        d => d.Contains("traditional-chinese"));
    }

    // build 가 검증할 클라이언트: 보통은 설정된 것(뺀 것 제외), 공식 패치 서버 exe 로 준비했으면 받아 둔 그 exe + 대만판
    [Fact]
    public void ClientsUsedFollowsHowMaterialWasPrepared()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        var korean = new GameClient("korean", Region.Korean, @"C:\k\ffxiv_dx11.exe", "2026.09.15.0000.0000");
        var tc = new GameClient("traditional-chinese", Region.TraditionalChinese, @"C:\t\ffxiv_dx11.exe", "2026.09.01.0000.0000");

        var local = new MaterialState(DateTime.Now, null, new() { ["traditional-chinese"] = tc.Version! }, [], true, true,
                                      Outdated: new() { ["korean"] = korean.Version! });
        Assert.Equal([tc], local.ClientsUsed(ws, [korean, tc]));

        var server = local with { Outdated = null, FromServer = true };
        Assert.Equal([tc], server.ClientsUsed(ws, [korean, tc]));
        // 받아 둔 exe 가 없으면(지웠거나 깨짐) 검증 없이 지나가지 않고 prepare 를 다시 하라고 한다
        var missing = server with { GameVersions = new() { ["korean"] = "2026.10.06.0000.0000" } };
        Assert.Contains("prepare 를 다시", Assert.Throws<ToolException>(() => missing.ClientsUsed(ws, [korean, tc])).Message);
    }

    // 공식 패치 서버 exe 로 준비할 때 받는 지역: 설정된 지역만(경로가 틀린 것도 — 지역 이름만 넘어온다), 모든 지역을 고르면
    // 공식 패치 서버가 있는 글로벌·중국·한국 (대만판 없음)
    [Fact]
    public void ServerRegionsFollowConfiguredRegions()
    {
        Assert.Equal([Region.Korean], Prepare.ServerRegions([Region.Korean], all: false));
        Assert.Equal([Region.SimplifiedChinese, Region.Korean],
                     Prepare.ServerRegions([Region.Korean, Region.SimplifiedChinese, Region.TraditionalChinese, null, Region.Korean], all: false));
        Assert.Empty(Prepare.ServerRegions([Region.TraditionalChinese], all: false));
        Assert.Equal([Region.Global, Region.SimplifiedChinese, Region.Korean], Prepare.ServerRegions([], all: true));
    }

    // 공식 패치 서버 exe 로 준비한 재료: 내 게임이 준비한 버전보다 옛 버전이면(업데이트 전) 차이가 아니지만, 새 버전이 됐거나
    // 기록에 없는 지역이면 알린다
    [Fact]
    public void ServerExeMaterialGoesStaleWhenMyGameMovesPast()
    {
        var state = new MaterialState(DateTime.Now, null, new() { ["korean"] = "2026.10.06.0000.0000" }, [], true, true, FromServer: true);
        var korean = new GameClient("korean", Region.Korean, @"C:\k\ffxiv_dx11.exe", "2026.10.06.0000.0000");
        Assert.Empty(state.Differences(@"C:\does\not\exist.dll", [korean with { Version = "2026.09.15.0000.0000" }]));
        Assert.Empty(state.Differences(@"C:\does\not\exist.dll", [korean]));
        Assert.Contains(state.Differences(@"C:\does\not\exist.dll", [korean with { Version = "2026.10.16.0000.0000" }]), d => d.Contains("패치되었습니다"));
        var global = new GameClient("global", Region.Global, @"C:\g\ffxiv_dx11.exe", "2026.10.16.0000.0000");
        Assert.Contains(state.Differences(@"C:\does\not\exist.dll", [korean, global]), d => d.Contains("기록에 없는"));
    }

    [Fact]
    public void ServerExeFlagRoundTrips()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        new MaterialState(DateTime.Now, "1.2", new() { ["global"] = "v" }, [], true, true, FromServer: true).Save(ws);
        Assert.True(MaterialState.Load(ws)!.FromServer);
        // 예전 기록(키 없음)은 내 게임으로 준비한 것
        File.WriteAllText(ws.StatePath, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(ws.StatePath), @",\s*""FromServer"": true", ""));
        Assert.DoesNotContain("FromServer", File.ReadAllText(ws.StatePath));
        Assert.False(MaterialState.Load(ws)!.FromServer);
    }

    [Theory]
    [InlineData("2026.10.02.0000.0000", "2026.10.06.0000.0000", true)]
    [InlineData("2026.10.06.0000.0000", "2026.10.06.0000.0000", false)]
    [InlineData("2026.10.06.0000.0000", "2026.10.02.0000.0000", false)]  // 패치 목록이 아직 갱신되지 않음 -> 오래된 것 아님
    [InlineData("2025.08.07.0000.0000", "2025.08.07.0000.0001", false)]  // 예전 패치는 끝자리만 다름 -> 같은 버전
    [InlineData("2026.10.06.0000.0000", "2026.10.06.0001.0000", true)]   // 같은 날 다시 나온 패치
    public void OutdatedComparisonIgnoresPatchSuffix(string local, string latest, bool older) =>
        Assert.Equal(older, Thaliak.IsOlder(local, latest));
}

// prepare 첫머리: 공식 패치 서버의 최신 버전보다 오래된 클라이언트는 이번 재료에서 뺀다
public class OutdatedClientTests
{
    private static GameClient Client(string region, string version) =>
        new(region, Region.Find(region), $@"C:\{region}\ffxiv_dx11.exe", version);

    [Fact]
    public void SplitsOutdatedClientsAndKeepsUnknownRegions()
    {
        var global = Client("global", "2026.10.06.0000.0000");
        var korean = Client("korean", "2026.10.02.0000.0000");
        var tc = Client("traditional-chinese", "2026.09.01.0000.0000");       // 최신 버전을 모르는 지역 -> 그대로
        var latest = new Dictionary<string, string>
        {
            ["global"] = "2026.10.06.0000.0001",                                   // 끝자리만 다름 -> 최신
            ["korean"] = "2026.10.06.0000.0000",
        };

        var (kept, outdated) = Prepare.SplitOutdated([global, korean, tc], latest);
        Assert.Equal([global, tc], kept);
        var o = Assert.Single(outdated);
        Assert.Equal(("korean", "2026.10.02.0000.0000", "2026.10.06.0000.0000"), (o.Name, o.Version, o.Latest));
        Assert.Equal("korean", o.Region.Name);
    }

    [Fact]
    public void AllOutdatedStops()
    {
        var korean = Client("korean", "2026.10.02.0000.0000");
        var ex = Assert.Throws<GameOutdatedException>(() =>
            Prepare.SplitOutdated([korean], new Dictionary<string, string> { ["korean"] = "2026.10.06.0000.0000" }));
        Assert.Equal("korean", Assert.Single(ex.Clients).Name);

        // 최신 버전을 하나도 확인하지 못했으면 그대로 진행
        var (kept, outdated) = Prepare.SplitOutdated([korean], new Dictionary<string, string>());
        Assert.Equal([korean], kept);
        Assert.Empty(outdated);
    }
}

// prepare 의 결론 (이 도구의 핵심 판단)
public class VerdictTests
{
    private static MaterialState.OpcodeInfo Info(int changed, int missing = 0) => new("src", changed, 26, missing);

    [Fact]
    public void PatchNeededWhenSomethingDiffers()
    {
        Assert.True(Prepare.Decide(false, false, false, [Info(0)], "1").Needed);       // 공식 후크가 깨짐
        Assert.True(Prepare.Decide(false, true, false, [Info(3)], "1").Needed);        // opcode 바뀜
        Assert.True(Prepare.Decide(false, true, true, [Info(0)], "1").Needed);         // 메모리 시그니처를 교체로 고침
        Assert.True(Prepare.Decide(false, null, false, [Info(3)], "1").Needed);        // 점검을 건너뛰었어도 opcode 가 다르면
    }

    [Fact]
    public void UndecidedWhenInformationIsMissing()
    {
        Assert.Null(Prepare.Decide(true, null, false, [Info(3)], "1").Needed);         // 시그니처 점검 실패 -> 바뀐 게 있어도 보류
        Assert.Null(Prepare.Decide(false, null, false, [Info(0)], "1").Needed);        // 후크 상태 모름
        Assert.Null(Prepare.Decide(false, true, false, [Info(0), null], "1").Needed);  // 한 클라이언트 opcode 못 구함
        Assert.Null(Prepare.Decide(false, true, false, [Info(0, missing: 2)], "1").Needed); // 출처에 빠진 이름
        Assert.Null(Prepare.Decide(false, true, false, [], "1").Needed);
    }

    [Fact]
    public void NotNeededOnlyWhenEverythingMatches() =>
        Assert.False(Prepare.Decide(false, true, false, [Info(0), Info(0)], "3.0.3.3").Needed);
}

// ElevationTests 가 게임이 켜진 것처럼 프로세스를 띄우므로 같은 묶음에서 차례로 (그 사이에 설치하면 거부된다)
[Collection("elevation")]
public class InstallTests
{
    [Fact]
    public void InstallsBacksUpAndRestoresExactly()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/output/FFXIV_ACT_Plugin.dll", "new plugin");
        tmp.File("project/output/deucalion-1.5.0.distrib.dll", "new deucalion");
        tmp.File("project/extras/OverlayPlugin/opcodes.jsonc", "new opcodes");
        string plugin = tmp.Dir("act/Plugins/FFXIV_ACT_Plugin_KR");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", "old plugin");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/deucalion-1.5.0.dll", "old deucalion (renamed by plugin)");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/keep.txt", "unrelated");
        string overlay = tmp.Dir("act/Plugins/OverlayPlugin/resources");
        tmp.File("act/Plugins/OverlayPlugin/resources/opcodes.jsonc", "old opcodes");
        tmp.File("act/Plugins/OverlayPlugin/OverlayPlugin.dll", "overlay");
        string Snapshot() => string.Join("|", new[] { plugin, overlay }
            .SelectMany(d => Directory.GetFiles(d).Order().Select(f => $"{Path.GetFileName(f)}={File.ReadAllText(f)}")));
        string before = Snapshot();

        var plan = Install.Plan(ws, plugin, overlay);
        Install.Run(ws, plan);
        Assert.Equal("new plugin", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
        Assert.True(File.Exists(Path.Combine(plugin, "deucalion-1.5.0.distrib.dll")));
        Assert.False(File.Exists(Path.Combine(plugin, "deucalion-1.5.0.dll")));     // 옛 것은 치움 (플러그인이 distrib 로 바꿔 씀)
        Assert.Equal("new opcodes", File.ReadAllText(Path.Combine(overlay, "opcodes.jsonc")));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(plugin, "keep.txt")));

        Install.Restore(ws);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void OfficialInstallUsesBasePluginAndLeavesOverlayAlone()
    {
        TestEnv.SkipIfActOrGameRunning();
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/input/plugin/FFXIV_ACT_Plugin.dll", "official latest");
        tmp.File("project/input/plugin/deucalion-1.5.1.distrib.dll", "official deucalion");
        tmp.File("project/input/plugin/_backup/3.0.3.1/FFXIV_ACT_Plugin.dll", "previous official"); // 하위 폴더는 넣지 않음
        tmp.File("project/output/FFXIV_ACT_Plugin.dll", "our patch");
        tmp.File("project/extras/OverlayPlugin/opcodes.jsonc", "our opcodes");
        string plugin = tmp.Dir("act/Plugins/FFXIV_ACT_Plugin_KR");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", "old official");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/deucalion-1.5.0.dll", "old deucalion");

        var plan = Install.Plan(ws, plugin, official: true);
        Assert.Null(plan.OverlayResources);
        Install.Run(ws, plan);
        Assert.Equal("official latest", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
        Assert.Equal("official deucalion", File.ReadAllText(Path.Combine(plugin, "deucalion-1.5.1.distrib.dll")));
        Assert.False(File.Exists(Path.Combine(plugin, "deucalion-1.5.0.dll")));

        Install.Restore(ws);
        Assert.Equal("old official", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
        Assert.True(File.Exists(Path.Combine(plugin, "deucalion-1.5.0.dll")));
        Assert.False(File.Exists(Path.Combine(plugin, "deucalion-1.5.1.distrib.dll")));
    }

    // 패치본을 설치할 수 있는 작업 폴더와 ACT 플러그인 폴더
    private static (TempDir Tmp, Workspace Ws, string Plugin) PatchedSetup()
    {
        var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        tmp.File("project/output/FFXIV_ACT_Plugin.dll", "our patch");
        tmp.File("project/output/deucalion-1.5.1.distrib.dll", "new deucalion");
        string plugin = tmp.Dir("act/Plugins/FFXIV_ACT_Plugin_KR");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", "old official");
        tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/deucalion-1.5.0.dll", "old deucalion");
        return (tmp, ws, plugin);
    }

    [Fact]
    public void RestoreRefusesWhenPluginWasReplacedAfterInstall()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = PatchedSetup();
        using (tmp)
        {
            Install.Run(ws, Install.Plan(ws, plugin, "-"));
            File.WriteAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll"), "newer official from 해루봇"); // 그 뒤 해루봇이 업데이트
            Assert.Throws<ToolException>(() => Install.Restore(ws));
            Assert.Equal("newer official from 해루봇", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
        }
    }

    [Fact]
    public void RestoredBackupIsNotOfferedAgainAndRenamedDistribIsRemoved()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = PatchedSetup();
        using (tmp)
        {
            Install.Run(ws, Install.Plan(ws, plugin, "-"));
            // ACT 를 한 번 켜면 플러그인이 distrib 을 deucalion-x.dll 로 이름을 바꾼다
            File.Move(Path.Combine(plugin, "deucalion-1.5.1.distrib.dll"), Path.Combine(plugin, "deucalion-1.5.1.dll"));
            Install.Restore(ws);
            Assert.Equal("old official", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
            Assert.True(File.Exists(Path.Combine(plugin, "deucalion-1.5.0.dll")));
            Assert.False(File.Exists(Path.Combine(plugin, "deucalion-1.5.1.dll")));
            Assert.Null(Install.LatestBackup(ws)); // 되돌린 백업은 다시 고르지 않는다
        }
    }

    [Fact]
    public void InstallRefusesWithoutDeucalion()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = PatchedSetup();
        using (tmp)
        {
            File.Delete(Path.Combine(ws.OutputDir, "deucalion-1.5.1.distrib.dll")); // 백신이 격리한 경우
            Assert.Throws<ToolException>(() => Install.Run(ws, Install.Plan(ws, plugin, "-")));
            Assert.True(File.Exists(Path.Combine(plugin, "deucalion-1.5.0.dll")));          // ACT 쪽은 그대로
            Assert.Equal("old official", File.ReadAllText(Path.Combine(plugin, "FFXIV_ACT_Plugin.dll")));
        }
    }

    [Fact]
    public void RestoreRejectsTamperedBackupRecord()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = PatchedSetup();
        using (tmp)
        {
            Install.Run(ws, Install.Plan(ws, plugin, "-"));
            string backup = Install.LatestBackup(ws)!;
            string json = File.ReadAllText(Path.Combine(backup, BackupManifest.FileName));
            File.WriteAllText(Path.Combine(backup, BackupManifest.FileName), json.Replace("\"FFXIV_ACT_Plugin.dll\"", "\"..\\\\..\\\\victim.dll\""));
            Assert.Throws<ToolException>(() => Install.Restore(ws));
        }
    }

    [Fact]
    public void RestoreRejectsRecordPointingOutsidePluginFolderOrWithoutHashes()
    {
        TestEnv.SkipIfActOrGameRunning();
        var (tmp, ws, plugin) = PatchedSetup();
        using (tmp)
        {
            Install.Run(ws, Install.Plan(ws, plugin, "-"));
            string backup = Install.LatestBackup(ws)!;
            string path = Path.Combine(backup, BackupManifest.FileName);
            string json = File.ReadAllText(path);

            // 플러그인이 없는 폴더를 가리킴 — 기록이 "FFXIV_ACT_Plugin.dll 을 넣었다"고 주장해도 디스크로 판단
            string victim = tmp.Dir("victim");
            tmp.File("victim/victim.dll", "keep");
            File.WriteAllText(path, json.Replace(System.Text.Json.JsonSerializer.Serialize(plugin).Trim('"'),
                                                 System.Text.Json.JsonSerializer.Serialize(victim).Trim('"')));
            Assert.Throws<ToolException>(() => Install.Restore(ws));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "victim.dll")));

            // 해시가 없는(예전 형식/조작된) 기록
            File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(json, "\"Hashes\":\\s*\\{[^}]*\\}", "\"Hashes\": null"));
            Assert.Throws<ToolException>(() => Install.Restore(ws));
        }
    }

    [Fact]
    public void RefusesWithoutBuildOutput()
    {
        using var tmp = new TempDir();
        Assert.Throws<ToolException>(() => Install.Plan(new Workspace(tmp.Path), tmp.Path, "-"));
    }
}
