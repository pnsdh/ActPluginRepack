namespace ActPluginRepack.Tests;

// 여러 곳이 같이 쓰는 판단 규칙 (한 곳에 모은 것)
public class SharedRulesTests
{
    // prepare 의 결론 하나로 CLI 종료 코드·GUI 흐름·status 가 정해진다
    [Theory]
    [InlineData(true, 0, true, "HooksBroken")]      // 점검 자체가 실패
    [InlineData(false, 1, true, "OpcodesMissing")]
    [InlineData(false, 0, true, "PatchNeeded")]
    [InlineData(false, 0, false, "NotNeeded")]
    [InlineData(false, 0, null, "Undecided")]
    public void PrepareOutcomeFollowsTheResult(bool sigFailed, int missing, bool? patchNeeded, string expected)
    {
        var r = new PrepareResult([], null, [.. Enumerable.Repeat("korean", missing)], patchNeeded, "") { SignatureCheckFailed = sigFailed };
        Assert.Equal(Enum.Parse<PrepareOutcome>(expected), r.Outcome); // (internal 형식이라 이름으로 받는다)
    }

    [Fact]
    public void ExitCodesFromErrors()
    {
        Assert.Equal(ExitCodes.AppsRunning, ExitCodes.Of(new AppsRunningException(["ACT"])));
        Assert.Equal(ExitCodes.NewerPlugin, ExitCodes.Of(new NewerPluginInstalledException("3.0.3.9", "3.0.3.3")));
        Assert.Equal(ExitCodes.Error, ExitCodes.Of(new ToolException("x")));
    }

    // 2/3/4 자리 버전이 섞여도 같은 규칙으로
    [Fact]
    public void VersionsCompareWithMissingPartsAsZero()
    {
        Assert.False(Versions.IsNewer(new Version(3, 0, 3), new Version(3, 0, 3, 0)));
        Assert.True(Versions.IsNewer(new Version(3, 0, 3, 1), new Version(3, 0, 3)));
        using var tmp = new TempDir();
        string a = tmp.File("a.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.4", "a"));
        string b = tmp.File("b.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3", "b"));
        Assert.True(Versions.IsNewer(a, b));
        Assert.False(Versions.IsNewer(b, a));
        Assert.False(Versions.IsNewer(a, Path.Combine(tmp.Path, "none.dll"))); // 읽지 못하면 새 것으로 보지 않는다
        Assert.Null(Versions.OfAssembly(tmp.File("broken.dll", "not a dll")));
    }

    [Fact]
    public void ConfigIsBrokenOnlyWhenUnreadable()
    {
        using var tmp = new TempDir();
        Assert.False(Config.IsBroken(tmp.Path));                          // 없음
        tmp.File("config.json", """{ "clients": { "korean": "Z:/none" } }""");
        Assert.False(Config.IsBroken(tmp.Path));                          // 내용이 틀린 것은 깨진 것이 아니다
        tmp.File("config.json", "[1, 2]");
        Assert.True(Config.IsBroken(tmp.Path));                           // 최상위가 { } 가 아님
        tmp.File("config.json", "{ broken");
        Assert.True(Config.IsBroken(tmp.Path));
    }

    // prepare 의 결론은 이름으로 저장한다 (숫자로 된 기록도 읽힌다)
    [Fact]
    public void PrepareOutcomeIsStoredByName()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("project"));
        Directory.CreateDirectory(Path.GetDirectoryName(ws.StatePath)!);
        new MaterialState(DateTime.Now, "3.0.3.3", [], [], true, true, Outcome: PrepareOutcome.HooksBroken).Save(ws);
        Assert.Contains("\"HooksBroken\"", File.ReadAllText(ws.StatePath));
        Assert.Equal(PrepareOutcome.HooksBroken, MaterialState.Load(ws)!.Outcome);

        File.WriteAllText(ws.StatePath, File.ReadAllText(ws.StatePath).Replace("\"HooksBroken\"", "1"));
        Assert.Equal(PrepareOutcome.NotNeeded, MaterialState.Load(ws)!.Outcome);
    }

    [Fact]
    public void GameVersionParts()
    {
        Assert.Equal("2026.10.06", GameVersion.DatePart("2026.10.06.0000.0001"));
        Assert.Equal("2026.10.06.0000", GameVersion.BuildPart("2026.10.06.0000.0001"));
        Assert.Null(GameVersion.BuildPart("2026.10"));
    }

    [Fact]
    public void SamePathIgnoresCaseAndTrailingSeparator()
    {
        Assert.True(Files.SamePath(@"C:\Games\KOREA\", @"c:\games\korea"));
        Assert.False(Files.SamePath(@"C:\Games\KOREA", @"C:\Games\KOREA2"));
        Assert.Equal("한국", ActLocator.HaerubotLabel(@"C:\ACT\Plugins\FFXIV_ACT_Plugin_KR"));
        Assert.Null(ActLocator.HaerubotLabel(@"C:\ACT\Plugins\Other"));
    }
}
