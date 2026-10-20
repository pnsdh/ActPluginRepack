namespace ActPluginRepack.Tests;

// 출처 보고: prepare 가 무엇을 어디서 가져왔는지 (화면의 [출처], CLI sources, extras\sources.txt)
public class SourceReportTests
{
    private static OpcodeCollector.Outcome Outcome(string source, int changed) => new(source, changed, 26, 0);

    [Fact]
    public void OpcodeLinesShowSourceAndCrossChecks()
    {
        var outcomes = new Dictionary<Region, OpcodeCollector.Outcome>
        {
            [Region.Global] = Outcome("karashiiro Global 7.57h", 26),
            [Region.Korean] = Outcome(OpcodeSet.WithFilled("localdiff korean a -> b", true), 3),
        };
        var comparisons = new Dictionary<Region, List<OpcodeCollector.Comparison>>
        {
            [Region.Global] = [new("opcodediff 7.57h", 38, []), new("localdiff global", 38, ["ActorCast: 7F vs 80"])],
            [Region.Korean] = [],
        };
        var items = SourceReport.OpcodeItems(outcomes, comparisons, [Region.Korean], new HashSet<Region> { Region.Global, Region.Korean, Region.TraditionalChinese });

        // 글로벌·한국·중국·대만 순서, 출처가 없는 지역은 공식 값 그대로
        Assert.Equal(["글로벌", "한국", "중국", "대만"], items.Select(i => i.Name));
        Assert.Contains("26개 중 26개가 바뀌었습니다", items[0].Text);
        Assert.Equal(["= opcodediff 7.57h: 38개 대조, 모두 같음", "≠ localdiff global: 38개 대조, 1개 다름 (ActorCast: 7F vs 80)"], items[0].Details);
        Assert.Contains("다른 출처로 채웠습니다", items[1].Text);
        Assert.Contains("공개 자료 없이", items[1].Text);
        Assert.Equal(["다른 출처 없음 (대조하지 못함)"], items[1].Details);
        Assert.Contains("공식 플러그인 값을 그대로", items[2].Text);   // 중국: 클라이언트 없음
        Assert.True(items[3].Warning);                                // 대만: 클라이언트가 있는데 못 구함 - 이전 파일이 남을 수 있다
        Assert.Contains("이전 파일", items[3].Text);
        Assert.DoesNotContain("—", string.Concat(items.Select(i => i.Text))); // 콘솔(cp949)에서 깨지지 않게
    }

    [Theory]
    [InlineData(null, "직접 넣은 것")]
    [InlineData("deucalion main a7316e7 (2026-10-13) - x", "upstream deucalion 커밋 a7316e7")]
    [InlineData("deucalion release 1.5.1 - x", "upstream deucalion 릴리스 1.5.1")]
    [InlineData("sigregen korean 2026.09.15 -> 2026.10.06 - x", "직전 버전 exe 와 비교해 다시 만듦")]
    public void SignatureSourceIsReadable(string? comment, string expected) =>
        Assert.StartsWith(expected, SourceReport.SourceText(comment));

    [Fact]
    public void SignatureLinesFollowTheReplacementFiles()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        Assert.Equal("모두 공식 플러그인의 시그니처를 그대로 씁니다.", Assert.Single(SourceReport.SignatureItems(ws, true)).Text);

        Directory.CreateDirectory(ws.SignaturesDir);
        File.WriteAllText(Path.Combine(ws.SignaturesDir, SignatureInput.FileName),
                          "# auto: sigregen global 2026.10.06 -> 2026.10.16 - x\nRecv|E8 $ { ' } 4C\n");
        var items = SourceReport.SignatureItems(ws, false);
        var recv = Assert.Single(items);
        Assert.Equal("패킷 후크 Recv", recv.Name);
        Assert.StartsWith("직전 버전 exe 와 비교해 다시 만듦", recv.Text);
    }

    // 교차 확인의 대조: 둘 이상에 있는 항목만 세고, 값이 갈리면 많은 쪽부터 어느 출처가 어떤 값인지
    [Fact]
    public void CrossCheckFindsTheSourceThatDisagrees()
    {
        string a = OpcodeNames.Used[0], b = OpcodeNames.Used[1], c = OpcodeNames.Used[2];
        OpcodeSet Set(string source, params (string Name, int Value)[] values) => new(source, values.ToDictionary(v => v.Name, v => v.Value));
        var (compared, diffs) = OpcodeCollector.Disagreements(
        [
            ("Yarukon", Set("Yarukon", (a, 0x7F), (b, 0x100))),
            ("localdiff", Set("localdiff", (a, 0x80), (b, 0x100), (c, 0x1))),   // a 만 다름, c 는 혼자라 대조하지 않음
            ("IDA", Set("IDA", (a, 0x7F), (b, 0x100))),
        ]);
        Assert.Equal(2, compared);
        Assert.Equal([$"{a}: 7F (Yarukon, IDA) / 80 (localdiff)"], diffs);
    }

    // 게임 데이터 교차 확인: 이름 목록 파일마다 견주고, 다수와 다른 출처를 어느 id 가 어떻게 다른지와 함께
    [Fact]
    public void CsvCrossCheckFindsTheSourceThatDisagrees()
    {
        Dictionary<string, Dictionary<string, string>> Lists(params (string File, string Id, string Name)[] rows) =>
            rows.GroupBy(r => r.File).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Id, r => r.Name));
        var (files, diffs) = CsvCrossCheck.Disagreements(
        [
            ("SharpVision", Lists(("ActionList_Korean.txt", "1", "공격"), ("StatusList_Korean.txt", "2", "강화"))),
            ("xiv-data-oxidizer", Lists(("ActionList_Korean.txt", "1", "공격"), ("StatusList_Korean.txt", "2", "강화"))),
            ("Ra-Workspace/ffxiv-datamining-ko", Lists(("ActionList_Korean.txt", "1", "공격!"), ("StatusList_Korean.txt", "2", "강화"))),
        ]);
        Assert.Equal(2, files);
        Assert.Equal(["ActionList_Korean.txt: 1개 id 다름 (SharpVision, xiv-data-oxidizer / Ra-Workspace/ffxiv-datamining-ko) 예: 1 '공격' / '공격!'"], diffs);
    }

    // 게임 데이터: 고른 것(input\csv)은 새로 구한 추출본의 복사본일 수 있어 독립된 출처로 세지 않는다
    [Fact]
    public void CsvChosenCopyIsNotAnIndependentSource()
    {
        var one = SourceReport.CrossCheckCsvItems([new("ko", [CsvCrossCheck.ChosenLabel, "SharpVision"], 4, [], [])]).Single();
        Assert.True(one.Unchecked);
        Assert.Contains("하나뿐", one.Text);
        var two = SourceReport.CrossCheckCsvItems([new("ko", [CsvCrossCheck.ChosenLabel, "SharpVision", "xiv-data-oxidizer"], 4, [], [])]).Single();
        Assert.False(two.Unchecked);
    }

    // 교차 확인: 지역마다 출처 목록과 출처끼리 다른 항목. 출처가 하나뿐이면 대조하지 못했다고
    [Fact]
    public void CrossCheckLinesListSourcesAndDifferences()
    {
        var items = SourceReport.CrossCheckOpcodeItems(
        [
            new(Region.Global, ["Yarukon", "localdiff", "IDA global"], 38, ["ActorCast: 7F (Yarukon, IDA global) / 80 (localdiff)"],
                ["karashiiro Global 7.57 (이번 버전인지 확인 못 함): 38개 중 26개가 Yarukon 와 다름"], HasIda: true),
            new(Region.Korean, ["Yarukon", "localdiff"], 38, [], [], HasIda: false),
            new(Region.SimplifiedChinese, ["Yarukon"], 0, [], [], HasIda: false),
            new(Region.TraditionalChinese, [], 0, [], [], HasIda: false),
        ], hasIda: true);
        Assert.Equal("opcode: 출처 3개의 38개 항목 중 1개가 다릅니다.", items[0].Text);
        Assert.Equal(["출처: Yarukon, localdiff, IDA global", "≠ ActorCast: 7F (Yarukon, IDA global) / 80 (localdiff)",
                      "참고 karashiiro Global 7.57 (이번 버전인지 확인 못 함): 38개 중 26개가 Yarukon 와 다름"], items[0].Details);
        Assert.True(items[0].Warning);
        Assert.False(items[0].Unchecked);
        // IDA 를 쓸 수 있었는데 그 지역에 IDA 결과가 없으면 대조가 빠진 것
        Assert.Equal("opcode: 출처 2개의 38개 항목이 모두 같습니다. IDA 결과는 없습니다.", items[1].Text);
        Assert.True(items[1].Unchecked);
        Assert.True(items[2].Unchecked);                    // 출처 하나뿐
        Assert.False(items[3].Unchecked);                   // 출처가 없으면 공식 값 그대로 — 고친 것이 없다
        Assert.Contains("대조하지 못했습니다", items[3].Text);
        Assert.True(SourceReport.CrossCheckOpcodeItems([], hasIda: false).Single().Unchecked); // IDA 를 쓸 수 없음

        // 이번 버전 출처 없이 고쳐 썼으면(확인되지 않은 karashiiro) 공식 값 그대로가 아니라 대조가 빠진 것
        var guessed = SourceReport.CrossCheckOpcodeItems([new(Region.TraditionalChinese, [], 0, [], ["karashiiro (이번 버전인지 확인 못 함): 견줄 확인된 출처가 없음"],
                                                              HasIda: false, Used: true)], hasIda: true).Single();
        Assert.True(guessed.Unchecked);
        Assert.DoesNotContain("공식 플러그인 값", guessed.Text);
        Assert.Contains(guessed.Details!, d => d.Contains("karashiiro"));

        // 클라이언트가 없고 같은 버전의 직접 추출도 없는 지역은 IDA 가 구조적으로 못 돈다 — 대조가 빠진 것으로 보지 않고 참고로
        var cn = SourceReport.CrossCheckOpcodeItems([new(Region.SimplifiedChinese, ["Yarukon", "opcodediff"], 38, [], [], HasIda: false, IdaPossible: false)],
                                                    hasIda: true).Single();
        Assert.False(cn.Unchecked);
        Assert.Contains(cn.Details!, d => d.Contains("직접 추출"));

        // 출처 보고에 "교차 확인" 칸 (하지 않았으면 칸이 없음)
        var report = new SourceReport(DateTime.Now, "v", [], null, null, null, null, []) { CrossCheck = items };
        Assert.Contains(report.Sections(), s => s.Title == "교차 확인");
        Assert.DoesNotContain((report with { CrossCheck = null }).Sections(), s => s.Title == "교차 확인");
    }

    [Fact]
    public void SavesJsonAndTextAndReadsBack()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        Assert.Null(SourceReport.Load(ws));
        new SourceReport(new DateTime(2026, 10, 20, 21, 0, 0), "임시 패치가 필요합니다.",
            [new("글로벌", "2026.10.16.0000.0000  공식 패치 서버에서 받은 exe")], "3.0.3.3",
            [new("글로벌", "karashiiro Global 7.57h - 26개 중 26개 바뀜", ["= opcodediff 7.57h: 38개 대조, 모두 같음"])],
            null, null, [@"extras\lemegeton.xml"]).Save(ws);

        var loaded = SourceReport.Load(ws)!;
        Assert.Equal("3.0.3.3", loaded.PluginVersion);
        Assert.Equal(["= opcodediff 7.57h: 38개 대조, 모두 같음"], loaded.Opcodes![0].Details);
        // 하지 않은 단계는 그렇다고
        Assert.StartsWith("이번 확인에서 하지 않았습니다", loaded.Sections().Single(s => s.Title == "시그니처").Items.Single().Text);

        string text = File.ReadAllText(SourceReport.TextPathOf(ws));
        Assert.Contains("[opcode]", text);
        Assert.Contains("      = opcodediff 7.57h: 38개 대조, 모두 같음", text);
        Assert.Contains(@"extras\lemegeton.xml", text);
    }
}
