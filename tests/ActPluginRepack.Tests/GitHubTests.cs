namespace ActPluginRepack.Tests;

// GitHub 의 조회 기억·막힘·토큰은 프로세스 전역이라 Http 시험과 같은 묶음에서 하나씩 돌린다
[Collection("http")]
public class GitHubTests : IDisposable
{
    public GitHubTests() => GitHub.ResetForTests();

    public void Dispose()
    {
        GitHub.TokenOverride = null;
        GitHub.ResetForTests();
    }

    // 토큰 유무에 따라 순서만 다르고(토큰이 있으면 API 먼저, 없으면 웹 먼저), 앞의 것이 실패하면 뒤의 것을 쓴다.
    // 이 PC 에 GITHUB_TOKEN / 저장한 토큰 / gh 로그인이 있는지와 상관없이 두 경우를 다 본다.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FetchFallsBackToTheOtherSource(bool hasToken)
    {
        GitHub.TokenOverride = () => hasToken ? ("test-token", "시험") : null;
        string first = hasToken ? "api" : "web", second = hasToken ? "web" : "api";
        string key = Guid.NewGuid().ToString();
        var calls = new List<string>();
        Func<string?> Ok(string name) => () => { calls.Add(name); return name; };
        Func<string?> Fail(string name) => () => { calls.Add(name); throw new ToolException($"{name} down"); };
        Func<string?> Of(string name, bool ok) => ok ? Ok(name) : Fail(name);

        // 앞의 것이 되면 뒤의 것은 부르지 않는다
        Assert.Equal(first, GitHub.Fetch(key + "a", "test", Of("api", true), Of("web", true)));
        Assert.Equal([first], calls);

        // 앞의 것이 실패하면 뒤의 것
        calls.Clear();
        Assert.Equal(second, GitHub.Fetch(key + "b", "test", Of("api", first != "api"), Of("web", first != "web")));
        Assert.Equal([first, second], calls);

        // 둘 다 실패하면 ToolException
        calls.Clear();
        Assert.Throws<ToolException>(() => GitHub.Fetch(key + "c", "test", Fail("api"), Fail("web")));
        Assert.Equal([first, second], calls);
    }

    // 같은 저장소의 패치본 릴리스(7.57, 7.56h1)와 시험판은 이 도구의 새 버전으로 보지 않는다
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("7.57", null)]
    [InlineData("7.56h1", null)]
    [InlineData("v1.2.3-beta", null)]
    [InlineData("1.2.3", null)]
    public void AppTagsAreOnlyVPrefixedReleases(string tag, string? expected) =>
        Assert.Equal(expected, AppInfo.ParseTag(tag)?.ToString(3));

    // 저장소의 Directory.Build.props 에 적힌 버전 (버전을 올려도 이 시험을 고치지 않게)
    private static Version PropsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string text = File.ReadAllText(Path.Combine(dir.FullName, "Directory.Build.props"));
        var m = System.Text.RegularExpressions.Regex.Match(text, @"<Version>([^<]+)</Version>");
        Assert.True(m.Success);
        var v = Version.Parse(m.Groups[1].Value);
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    [Fact]
    public void AppVersionComparison()
    {
        Assert.True(AppInfo.IsNewer(new Version(2, 0, 1), new Version(2, 0, 0, 0)));   // 어셈블리 버전은 4자리
        Assert.False(AppInfo.IsNewer(new Version(2, 0, 0), new Version(2, 0, 0, 0)));
        Assert.False(AppInfo.IsNewer(new Version(0, 9), new Version(1, 0)));
        Assert.Equal(PropsVersion(), AppInfo.Version);                                  // Directory.Build.props 와 같음
    }

    [Fact]
    public void FetchRemembersResults()
    {
        string key = Guid.NewGuid().ToString();
        int calls = 0;
        string? Count() { calls++; return null; } // "릴리스 없음" 같은 null 결과도 기억한다
        Assert.Null(GitHub.Fetch(key, "test", Count, Count));
        Assert.Null(GitHub.Fetch(key, "test", Count, Count));
        Assert.Equal(1, calls);
    }
}
