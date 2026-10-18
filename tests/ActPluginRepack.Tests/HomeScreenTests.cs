using static ActPluginRepack.Tests.TestReports;

namespace ActPluginRepack.Tests;

// GUI 간편 모드 첫 화면의 판단: 지금 상태 -> 보일 안내와 [공식 플러그인으로 되돌리기] (HomeScreen)
public class HomeScreenTests
{
    // config: null(문제 없음) / "bad"(게임 위치가 틀림) / "broken"(설정 파일이 문법부터 깨짐)
    [Theory]
    [InlineData("broken", true, "ConfigProblem", false, "ConfigBroken")]
    [InlineData("bad", true, "ConfigProblem", false, "ConfigProblem")]
    [InlineData("broken", false, "ConfigProblem", false, "ConfigBroken")]  // 설정 문제가 ACT 보다 먼저
    [InlineData("bad", true, "Patched", false, "ConfigProblem")]           // 설정 오류가 있으면 단계는 보지 않는다
    [InlineData(null, false, "OfficialMatches", false, "ActMissing")]      // ACT 를 모르면 단계는 보지 않는다
    [InlineData(null, false, "Patched", false, "ActMissing")]
    [InlineData(null, true, "Patched", false, "Patched")]
    [InlineData(null, true, "OfficialMatches", true, "OfficialUpdate")]
    [InlineData(null, true, "OfficialMatches", false, "NotNeeded")]
    [InlineData(null, true, "NewerInstalled", false, "NewerInstalled")]
    [InlineData(null, true, "NotPrepared", false, "Default")]
    [InlineData(null, true, "Stale", false, "Default")]
    [InlineData(null, true, "NotReady", false, "Default")]
    [InlineData(null, true, "Undecided", false, "Default")]
    [InlineData(null, true, "NotBuilt", false, "Default")]
    [InlineData(null, true, "NotInstalled", false, "Default")]
    [InlineData(null, true, "ConfigProblem", false, "Default")]            // (설정 문제는 ConfigError 로 본다 — 클라이언트가 없기만 한 경우)
    [InlineData(null, true, "NotInstalled", true, "Default")]              // 공식 최신본으로 바꾸는 것은 패치가 필요 없을 때만
    public void ShowsStateForStatus(string? config, bool act, string stage, bool officialUpdate, string expected)
    {
        var s = Status(Enum.Parse<StatusStage>(stage), act: act, officialUpdate: officialUpdate,
                       configError: config == null ? null : "설정 오류", prepared: Material(),
                       actError: act ? null : "ACT 에 FFXIV 플러그인이 없습니다");
        var d = HomeScreen.Decide(s, () => config == "broken");
        Assert.Equal(Enum.Parse<HomeStatus>(expected), d.Status); // (internal 형식이라 이름으로 받는다)
    }

    // 설정 파일이 깨졌는지는 설정 오류가 있을 때만 묻는다 (파일을 읽는다)
    [Fact]
    public void AsksConfigBrokenOnlyOnConfigError()
    {
        int asked = 0;
        HomeScreen.Decide(Status(StatusStage.Patched, prepared: Material()), () => { asked++; return true; });
        Assert.Equal(0, asked);
        Assert.Equal(HomeStatus.ConfigBroken, HomeScreen.Decide(Status(configError: "x"), () => { asked++; return true; }).Status);
        Assert.Equal(1, asked);
    }

    // ACT 를 찾지 못한 까닭: 종류가 있으면 그것, 없으면 까닭 문장이 없을 때만 "등록돼 있지 않음" (있으면 그 문장을 보인다)
    [Theory]
    [InlineData(null, null, "NotRegistered")]
    [InlineData(null, "ACT 설정에 없음", null)]
    [InlineData("NotRegistered", "등록 안 됨", "NotRegistered")]
    [InlineData("MultipleEnabled", "여러 개", "MultipleEnabled")]
    [InlineData("FileMissing", "파일 없음", "FileMissing")]
    [InlineData("ConfigUnreadable", "깨짐", "ConfigUnreadable")]
    [InlineData("ChineseOnly", "중국", "ChineseOnly")]
    public void ActMissingCarriesProblem(string? problem, string? error, string? expected)
    {
        var s = Status(act: false, actProblem: problem == null ? null : Enum.Parse<ActProblem>(problem), actError: error);
        var d = HomeScreen.Decide(s, () => false);
        Assert.Equal(HomeStatus.ActMissing, d.Status);
        Assert.Equal(expected == null ? null : Enum.Parse<ActProblem>(expected), d.ActProblem);
    }

    // 확인 안 된 opcode 로 고쳤으면 다시 누를 계기를 남긴다
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchedTellsUnconfirmed(bool unconfirmed)
    {
        var d = HomeScreen.Decide(Status(StatusStage.Patched, prepared: Material(unconfirmed)), () => false);
        Assert.Equal(HomeStatus.Patched, d.Status);
        Assert.Equal(unconfirmed, d.Unconfirmed);
    }

    // [공식 플러그인으로 되돌리기]: ACT 에 이 프로그램의 패치본이 있고 받아 둔 공식 플러그인이 있을 때만 (어느 안내이든)
    [Theory]
    [InlineData("Patched", true, true, true)]
    [InlineData("Patched", true, false, false)]   // 받아 둔 공식 플러그인이 없음
    [InlineData("Patched", false, true, false)]   // 패치본이 아님
    [InlineData("OfficialMatches", true, true, true)]
    [InlineData("Stale", true, true, true)]
    [InlineData("NotReady", false, true, false)]
    public void CanRestoreWhenPatchAndOfficialExist(string stage, bool patchInstalled, bool basePlugin, bool expected)
    {
        var s = Status(Enum.Parse<StatusStage>(stage), patchInstalled: patchInstalled, basePlugin: basePlugin ? "3.0.3.3" : null,
                       officialUpdate: stage == "OfficialMatches", prepared: Material());
        Assert.Equal(expected, HomeScreen.Decide(s, () => false).CanRestore);
        // 설정 오류·ACT 를 찾지 못한 안내에서도 같은 판단
        Assert.Equal(expected, HomeScreen.Decide(s with { ConfigError = "x" }, () => true).CanRestore);
        Assert.Equal(expected, HomeScreen.Decide(s with { Act = null, ActError = "x" }, () => false).CanRestore);
    }
}
