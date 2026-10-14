namespace ActPluginRepack.Tests;

// config.json 읽기: 선택 항목의 값 종류와 잘못된 값
public class ConfigLoadTests
{
    private static AppConfig Load(TempDir tmp, string json)
    {
        tmp.File("config.json", json);
        return Config.Load(tmp.Path);
    }

    [Fact]
    public void OxidizerTrueStringOrFalse()
    {
        using var tmp = new TempDir();
        Assert.Equal("true", Load(tmp, """{ "oxidizer": true, "ida": false }""").Oxidizer);        // tools\ 에 받아 빌드
        Assert.Equal(Path.Combine(tmp.Path, "clones", "oxidizer"),
                     Load(tmp, """{ "oxidizer": "clones/oxidizer", "ida": false }""").Oxidizer);  // 프로젝트 기준 경로
        Assert.Null(Load(tmp, """{ "oxidizer": false, "ida": false }""").Oxidizer);
        Assert.Null(Load(tmp, """{ "ida": false }""").Oxidizer);
        Assert.Throws<ToolException>(() => Load(tmp, """{ "oxidizer": 1, "ida": false }"""));
        Assert.Throws<ToolException>(() => Load(tmp, """{ "oxidizer": "  ", "ida": false }"""));
    }

    [Theory]
    [InlineData("\"traditional-chinese\"")]  // opcode 가 다른 지역
    [InlineData("\"mars\"")]
    [InlineData("3")]
    [InlineData("null")]
    public void InvalidIdaTargetIsRejected(string value)
    {
        using var tmp = new TempDir();
        Assert.Throws<ToolException>(() => Load(tmp, $$"""{ "ida": false, "idaTarget": {{value}} }"""));
    }

    [Fact]
    public void IdaTargetDefaultAndCaseInsensitive()
    {
        using var tmp = new TempDir();
        Assert.Equal("simplified-chinese", Load(tmp, """{ "ida": false }""").IdaTarget);
        Assert.Equal("korean", Load(tmp, """{ "ida": false, "idaTarget": "Korean" }""").IdaTarget);
    }

    [Theory]
    [InlineData("\"false\"")]
    [InlineData("0")]
    [InlineData("null")]
    public void LocalDiffMustBeBool(string value)
    {
        using var tmp = new TempDir();
        Assert.Throws<ToolException>(() => Load(tmp, $$"""{ "ida": false, "localDiff": {{value}} }"""));
    }

    [Fact]
    public void IdaFalseIsNullAndNonPathIsRejected()
    {
        using var tmp = new TempDir();
        var off = Load(tmp, """{ "ida": false }""");
        Assert.Null(off.Ida);
        Assert.False(off.IdaDetected);
        Assert.Throws<ToolException>(() => Load(tmp, """{ "ida": true }"""));
        Assert.Throws<ToolException>(() => Load(tmp, """{ "ida": "" }"""));
    }

    [Fact]
    public void SameRegionTwiceDifferingInCaseIsClientError()
    {
        using var tmp = new TempDir();
        tmp.File("kr/game/ffxiv_dx11.exe", "x");
        tmp.File("kr/game/ffxivgame.ver", "2026.10.06.0000.0000\r\n");
        var config = Load(tmp, """{ "ida": false, "clients": { "korean": "kr", "Korean": "kr" } }""");
        var client = Assert.Single(config.Clients);
        Assert.Equal("korean", client.Name);
        Assert.Equal("2026.10.06.0000.0000", client.Version);                // 줄바꿈은 무시
        Assert.Contains("두 번", Assert.Single(config.ClientErrors));
    }

    [Fact]
    public void DuplicateAfterBadPathIsStillReportedOnce()
    {
        // 첫 항목의 경로가 틀려도 같은 지역 두 번째 항목을 쓰지 않는다 (어느 쪽이 맞는지 모름)
        using var tmp = new TempDir();
        tmp.File("kr/game/ffxiv_dx11.exe", "x");
        var config = Load(tmp, """{ "ida": false, "clients": { "korean": "nope", "KOREAN": "kr" } }""");
        Assert.Empty(config.Clients);
        Assert.Equal(2, config.ClientErrors.Count);
        Assert.StartsWith("korean:", config.ClientErrors[0]);
        Assert.Contains("두 번", config.ClientErrors[1]);
    }

    [Fact]
    public void ClientPathFormsAndBadVersionFile()
    {
        using var tmp = new TempDir();
        tmp.File("gl/game/ffxiv_dx11.exe", "x");
        tmp.File("gl/game/ffxivgame.ver", "garbage");
        string exe = Path.Combine(tmp.Path, "gl", "game", "ffxiv_dx11.exe");
        foreach (string path in new[] { "gl", "gl/game", "gl/game/ffxiv_dx11.exe" }) // 설치 폴더 / game 폴더 / exe
        {
            var client = Assert.Single(Load(tmp, $$"""{ "ida": false, "clients": { "global": "{{path}}" } }""").Clients);
            Assert.Equal(exe, client.ExePath);
            Assert.Null(client.Version);                                       // 형식이 틀린 버전은 버림
        }
    }

    [Fact]
    public void NonStringClientPathIsClientError()
    {
        using var tmp = new TempDir();
        var config = Load(tmp, """{ "ida": false, "clients": { "korean": 1 } }""");
        Assert.Empty(config.Clients);
        Assert.Contains("clients.korean", Assert.Single(config.ClientErrors));
    }

    [Theory]
    [InlineData("""{ "clients": [] }""")]                       // 객체가 아님
    [InlineData("""{ "clients": { "mars": "x" } }""")]          // 모르는 지역
    [InlineData("""{ "sharpVision": false }""")]
    [InlineData("{ broken")]
    public void BadConfigIsToolException(string json)
    {
        using var tmp = new TempDir();
        Assert.Throws<ToolException>(() => Load(tmp, json));
    }
}
