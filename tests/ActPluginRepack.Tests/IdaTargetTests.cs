namespace ActPluginRepack.Tests;

// IDA 로 분석할 exe 고르기: 통합 클라이언트 지역은 같은 버전의 대상 지역(기본 중국판) exe — 설정에 있으면 그것,
// 없으면 받고, 받지 못하면 이 클라이언트 exe. 대만판처럼 opcode 가 다른 지역은 언제나 자기 exe.
public class IdaTargetTests
{
    private const string V = "2026.10.06.0000.0000";
    private static readonly Region Global = Region.Find("global")!, Korean = Region.Find("korean")!,
                                   Chinese = Region.Find("simplified-chinese")!, Taiwan = Region.Find("traditional-chinese")!;

    private static GameClient Client(Region r, string version = V) => new(r.Name, r, $@"C:\{r.Name}\game\ffxiv_dx11.exe", version);

    private static AppConfig Config(params GameClient[] clients) => new([.. clients], null, null);

    private static string NoDownload(Region r, string v) => throw new Xunit.Sdk.XunitException("받으면 안 됨");

    [Fact]
    public void UsesConfiguredClientOfTheTargetRegionWhenSameVersion()
    {
        var korean = Client(Korean);
        var chinese = Client(Chinese);
        Assert.Equal((Chinese, chinese.ExePath), OpcodeCollector.IdaTarget(korean, Config(korean, chinese), NoDownload));
    }

    [Fact]
    public void DownloadsTheTargetExeWhenNotConfiguredOrDifferentVersion()
    {
        var korean = Client(Korean);
        var olderChinese = Client(Chinese, "2026.10.02.0000.0000");
        var asked = new List<(Region, string)>();
        string Download(Region r, string v) { asked.Add((r, v)); return @"D:\snap\cn.exe"; }

        Assert.Equal((Chinese, @"D:\snap\cn.exe"), OpcodeCollector.IdaTarget(korean, Config(korean, olderChinese), Download));
        Assert.Equal([(Chinese, V)], asked);
    }

    [Fact]
    public void FallsBackToOwnExeWhenDownloadFails()
    {
        var global = Client(Global);
        Assert.Equal((Global, global.ExePath),
            OpcodeCollector.IdaTarget(global, Config(global), (_, _) => throw new NetworkException("끊김")));
    }

    [Fact]
    public void KeepsOwnExeForTargetRegionTaiwanAndOtherTargets()
    {
        var chinese = Client(Chinese);
        Assert.Equal((Chinese, chinese.ExePath), OpcodeCollector.IdaTarget(chinese, Config(chinese), NoDownload)); // 이미 대상 지역
        var taiwan = Client(Taiwan);
        Assert.Equal((Taiwan, taiwan.ExePath), OpcodeCollector.IdaTarget(taiwan, Config(taiwan), NoDownload));      // opcode 가 다른 지역

        var korean = Client(Korean);
        var toTaiwan = Config(korean) with { IdaTarget = "traditional-chinese" };                                    // 대상으로 쓸 수 없는 지역
        Assert.Equal((Korean, korean.ExePath), OpcodeCollector.IdaTarget(korean, toTaiwan, NoDownload));
        var toKorean = Config(korean) with { IdaTarget = "korean" };
        Assert.Equal((Korean, korean.ExePath), OpcodeCollector.IdaTarget(korean, toKorean, NoDownload));
    }
}
