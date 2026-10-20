namespace ActPluginRepack.Tests;

public class HotfixTests
{
    [Fact]
    public void ReleaseNotesFromRecords()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        tmp.File("input/signatures/deucalion.txt",
            "# auto: deucalion main a7316e7 (2026-10-13) - korean 3, global 3\nRecv|E8 $ { ' } 4C 8B 47 10 44 8B 4F 1C\n# 직접\nSend|40 53\n");
        var prepared = new MaterialState(DateTime.Now, "3.0.3.1",
            new() { ["korean"] = "2026.10.06.0000.0000", ["global"] = "2026.10.06.0000.0000" },
            new()
            {
                ["korean"] = new("Yarukon CN_2026.10.06", 26, 26),
                ["global"] = new("Yarukon CN_2026.10.06", 26, 26),
            },
            HooksOkOfficially: false, PatchNeeded: true);
        var built = new BuildInfo(DateTime.Now, "3.0.3.1", prepared.GameVersions, true);
        tmp.File("input/overrides/" + Rsv.FileName, "_rsv_1|a\n");
        tmp.File("input/overrides/global_mine.txt", "k|v\nk2|v2\n");
        tmp.File("input/overrides/Global_case.txt", "k|v\n");   // 빌드가 넣지 않는 파일 (접두어는 대소문자까지 — Repacker)
        tmp.File("input/overrides/notes.txt", "k|v\n");

        string notes = HotfixPackage.Notes(ws, "7.57", prepared, built, ["FFXIV_ACT_Plugin.dll", "deucalion-1.5.0.distrib.dll"]);

        Assert.Contains("## 7.57 임시 패치본", notes);
        Assert.Contains("랭킹에는 반영되지 않습니다", notes);
        Assert.Contains("한국·글로벌 `2026.10.06.0000.0000`", notes);          // 같은 버전은 한 번
        Assert.Contains("[`3.0.3.1`](https://github.com/ravahn/FFXIV_ACT_Plugin/releases/tag/3.0.3.1)", notes);
        Assert.Contains("[`1.5.0`](https://github.com/ff14wed/deucalion/releases/tag/1.5.0)", notes);
        Assert.Contains("opcode (한국, 글로벌): [Yarukon CN_2026.10.06](https://github.com/Yarukon/FFXIVNetworkOpcodes/tree/master/output/CN_2026.10.06) — 26개 중 26개 변경", notes);
        Assert.Contains("Recv ([deucalion `a7316e7`](https://github.com/ff14wed/deucalion/commit/a7316e7))", notes);
        Assert.Contains("Send (직접 입력)", notes);
        Assert.DoesNotContain("메모리 시그니처", notes);
        Assert.Contains("가려진 이름(RSV): ACT 로그에서 가져온 1개 추가", notes);
        Assert.Contains("이름 override: `global_mine.txt` 2개", notes);
        Assert.DoesNotContain("Global_case", notes);                    // 빌드가 넣지 않는 것은 적지 않는다
        Assert.DoesNotContain("notes.txt", notes);
    }

    [Theory]
    [InlineData("opcodediff 7.57", "[opcodediff 7.57](https://github.com/xivdev/opcodediff/blob/master/diffs/7.57.diff.json)")]
    [InlineData("karashiiro KR 7.57", "[karashiiro KR 7.57](https://github.com/karashiiro/FFXIVOpcodes/blob/master/opcodes.min.json)")]
    [InlineData("Yarukon CN_2026.10.06 +보충", "[Yarukon CN_2026.10.06](https://github.com/Yarukon/FFXIVNetworkOpcodes/tree/master/output/CN_2026.10.06) (빠진 항목은 다른 출처로 보충)")]
    [InlineData("localdiff korean 7.57", "[opcodediff](https://github.com/xivdev/opcodediff) 직접 실행 (korean 7.57)")]
    public void OpcodeSourcesLinkToWhatWasUsed(string source, string expected) =>
        Assert.Equal(expected, HotfixPackage.OpcodeSourceLink(source));

    [Fact]
    public void SignatureSourcesLink()
    {
        Assert.Equal("[deucalion 1.5.1](https://github.com/ff14wed/deucalion/releases/tag/1.5.1)",
                     HotfixPackage.SignatureSourceLink("deucalion release 1.5.1 - korean 3"));
        Assert.Equal("직전 버전 exe 와 비교해 재생성", HotfixPackage.SignatureSourceLink("sigregen korean 2026.09.15 -> x"));
        Assert.Equal("직접 입력", HotfixPackage.SignatureSourceLink(null));
    }

    // 틀린 패치본을 올리지 않게: 빌드 기록이 없거나, 재료가 지금 것과 다르거나(오래됨), 빌드가 prepare 보다 먼저면 멈춘다
    [Fact]
    public void RefusesStaleOrMissingBuild()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        tmp.File("config.json", """{ "ida": false }""");                     // 클라이언트 없음 (게임 버전 비교는 건너뜀)
        Assert.Contains("빌드 기록", Assert.Throws<ToolException>(() => HotfixPackage.Create(ws, "7.57")).Message);

        // input\plugin 은 3.0.3.3 인데 prepare 기록은 3.0.3.1 기준 — 재료가 오래됨
        tmp.File("input/plugin/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3"));
        tmp.File("output/FFXIV_ACT_Plugin.dll", "patched");
        var prepared = new MaterialState(DateTime.Now.AddMinutes(-10), "3.0.3.1", [], [], HooksOkOfficially: false, PatchNeeded: true);
        prepared.Save(ws);
        new BuildInfo(DateTime.Now, "3.0.3.1", [], true).Save(ws.OutputDir);
        var stale = Assert.Throws<ToolException>(() => HotfixPackage.Create(ws, "7.57"));
        Assert.Contains("베이스 플러그인이 바뀌었습니다", stale.Message);
        Assert.False(Directory.Exists(ws.HotfixDir("7.57")));

        // 재료는 지금 것인데 빌드가 마지막 prepare 보다 먼저
        (prepared with { PluginVersion = "3.0.3.3" }).Save(ws);
        new BuildInfo(DateTime.Now.AddMinutes(-20), "3.0.3.3", [], true).Save(ws.OutputDir);
        var early = Assert.Throws<ToolException>(() => HotfixPackage.Create(ws, "7.57"));
        Assert.Contains("빌드가 마지막 prepare 보다 먼저", early.Message);
        Assert.False(Directory.Exists(ws.HotfixDir("7.57")));

        // 둘 다 맞으면 만든다
        new BuildInfo(DateTime.Now, "3.0.3.3", [], true).Save(ws.OutputDir);
        var made = HotfixPackage.Create(ws, "7.57");
        Assert.True(File.Exists(made.ZipPath));
        Assert.Contains(made.Warnings, w => w.Contains("교차 확인 없이"));
    }

    // 배포 전 점검: 교차 확인에서 다른 것이 있었으면 경고, 모두 일치했으면 릴리스 설명에 남긴다
    [Fact]
    public void CrossCheckResultGoesIntoWarningsAndNotes()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        tmp.File("config.json", """{ "ida": false }""");
        tmp.File("input/plugin/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3"));
        tmp.File("output/FFXIV_ACT_Plugin.dll", "patched");
        var prepared = new MaterialState(DateTime.Now.AddMinutes(-1), "3.0.3.3", [], [], HooksOkOfficially: true, PatchNeeded: true,
                                         CrossCheck: ["글로벌", "Recv"]);
        prepared.Save(ws);
        var built = new BuildInfo(DateTime.Now, "3.0.3.3", [], true);
        built.Save(ws.OutputDir);

        var made = HotfixPackage.Create(ws, "7.57");
        Assert.Contains(made.Warnings, w => w.Contains("다른 것이 있었습니다: 글로벌, Recv"));
        Assert.DoesNotContain("### 확인", File.ReadAllText(made.NotesPath));

        var clean = prepared with { CrossCheck = [] };
        Assert.Contains("### 확인", HotfixPackage.Notes(ws, "7.57", clean, built, ["FFXIV_ACT_Plugin.dll"]));
        Assert.DoesNotContain("### 확인", HotfixPackage.Notes(ws, "7.57", clean with { CrossCheck = null }, built, ["FFXIV_ACT_Plugin.dll"]));

        // 대조하지 못한 것이 있으면 "확인"으로 적지 않고 경고한다 (하지 않은 분류, 짝이 없던 항목)
        var partial = clean with { CrossCheckGaps = ["게임 데이터 (하지 않았거나 실패)"] };
        Assert.DoesNotContain("### 확인", HotfixPackage.Notes(ws, "7.57", partial, built, ["FFXIV_ACT_Plugin.dll"]));
        partial.Save(ws);
        built.Save(ws.OutputDir);
        Assert.Contains(HotfixPackage.Create(ws, "7.57").Warnings, w => w.Contains("대조하지 못한 것이 있습니다: 게임 데이터"));
    }

    [Theory]
    [InlineData("7.58", true)]
    [InlineData("7.58h1", true)]
    [InlineData("7.58.", false)]
    [InlineData("7 58", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void PatchNumberRules(string tag, bool ok)
    {
        Assert.Equal(ok, HotfixPackage.TagProblem(tag) == null);
    }
}
