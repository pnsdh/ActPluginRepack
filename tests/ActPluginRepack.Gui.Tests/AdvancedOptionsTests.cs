using System.Text.Json;
using ActPluginRepack.Gui.ViewModels;

namespace ActPluginRepack.Gui.Tests;

// 고급 모드에서 고른 것 (gui.json 에 저장): 기본값, "기본과 다른 선택" 줄, 저장·읽기
public class AdvancedOptionsTests
{
    private static List<string> Diff(AdvancedOptions o) => o.Differences(name => $"<{name}>", region => $"[{region}]");

    [Fact]
    public void DefaultsHaveNothingToReport() => Assert.Empty(Diff(new AdvancedOptions()));

    [Fact]
    public void ReportsWhatDiffersFromDefaults()
    {
        var o = new AdvancedOptions
        {
            Csv = false, CrossCheck = true, ServerExe = true, ServerAllRegions = true, IdaTarget = "global",
            DoInstall = true, InstallOverlay = false, DisabledSources = ["ida", "xivdata"],
        };
        Assert.Equal(
        [
            "IDA 분석 대상 [global]", "공식 패치 서버 exe (설치하지 않은 지역까지)", "교차 확인", "설치", "OverlayPlugin opcode 넣기 끔",
            "데이터 소스 2개 끔 (<ida>, <xivdata>)",
        ], Diff(o)); // CSV 수집은 서버 exe 모드에서는 어차피 하지 않으므로 적지 않는다

        // 꺼진 상위 작업의 하위 옵션은 적지 않는다 (자동 복구는 시그니처 점검, CSV 다시 변환은 build 에 딸림)
        Assert.Equal(["시그니처 점검 끔", "build 끔"], Diff(new AdvancedOptions { Sigcheck = false, Repair = false, DoBuild = false, BuildCsv = false }));
    }

    // gui.json 에 그대로 저장되고 읽힌다 (예전 gui.json 에는 없음 -> null, 기본값으로)
    [Fact]
    public void RoundTripsInGuiSettings()
    {
        var settings = new GuiSettings { Advanced = true, Options = new AdvancedOptions { CrossCheck = true, DisabledSources = ["ida"] } };
        var loaded = JsonSerializer.Deserialize<GuiSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(loaded.Options!.CrossCheck);
        Assert.Equal(["ida"], loaded.Options.DisabledSources);
        Assert.True(loaded.Options.DoBuild); // 기본값
        Assert.Null(JsonSerializer.Deserialize<GuiSettings>("""{ "Advanced": true, "Workspace": null, "Hotfix": true }""")!.Options);
    }

    // 고른 것(Options)만 형식이 틀렸으면 그것만 버린다 — 작업 폴더·모드 설정은 그대로
    [Fact]
    public void BrokenOptionsDoNotResetOtherSettings()
    {
        var s = GuiSettings.Parse("""{ "Advanced": true, "Workspace": "D:\\work", "Hotfix": true, "Options": { "CrossCheck": "yes" } }""");
        Assert.True(s.Advanced);
        Assert.Equal(@"D:\work", s.Workspace);
        Assert.True(s.Hotfix);
        Assert.Null(s.Options);
        Assert.False(GuiSettings.Parse("not json").Advanced); // 통째로 깨졌으면 기본값
    }
}
