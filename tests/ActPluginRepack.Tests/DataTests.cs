namespace ActPluginRepack.Tests;

public class CsvConverterTests
{
    private static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    [Fact]
    public void ConvertsOxidizerFormat()
    {
        using var tmp = new TempDir();
        string csv = tmp.Dir("ko");
        tmp.File("ko/Action.csv", "#,Name,Icon\r\n0,,1\r\n7,공격,2\r\n31,\"쉼표, 있는 이름\",3\r\n32,,4\r\n");
        tmp.File("ko/PlaceName.csv", "#,Name\r\n0,\r\n10,림사 로민사\r\n");
        tmp.File("ko/TerritoryType.csv", "#,PlaceName\r\n128,10\r\n129,0\r\n");
        tmp.File("ko/ContentFinderCondition.csv", "#,TerritoryType,Name\r\n1,128,던전A\r\n2,128,던전B\r\n3,0,없음\r\n");
        string outDir = tmp.Dir("out");

        CsvConverter.Convert(csv, outDir, lang: null, ["Action", "Territory", "ContentFinder"]);

        Assert.Equal("7|공격\r\n1f|쉼표, 있는 이름\r\n", Read(outDir, "ActionList_Korean.txt")); // 16진수, 빈 이름 제외
        Assert.Equal("128|림사 로민사\r\n", Read(outDir, "TerritoryList_Korean.txt"));             // 10진수, PlaceName 조인
        Assert.Equal("128|던전A\r\n", Read(outDir, "ContentFinderList_Korean.txt"));             // TerritoryType 키, 첫 행
    }

    [Fact]
    public void ReadsSaintCoinachFormat()
    {
        using var tmp = new TempDir();
        string csv = tmp.Dir("tc");
        tmp.File("tc/Action.csv", "key,0,1\r\n#,Name,Icon\r\nint32,str,Image\r\n2,衝刺,4\r\n");
        string outDir = tmp.Dir("out");
        CsvConverter.Convert(csv, outDir, lang: null, ["Action"]);
        Assert.Equal("2|衝刺\r\n", Read(outDir, "ActionList_TraditionalChinese.txt"));
    }

    [Fact]
    public void RefusesEmptyOrBrokenInput()
    {
        using var tmp = new TempDir();
        string csv = tmp.Dir("ko");
        string outDir = tmp.Dir("out");
        tmp.File("ko/Action.csv", "#,Name\r\n");
        Assert.Throws<ToolException>(() => CsvConverter.Convert(csv, outDir, null, ["Action"])); // 0건
        tmp.File("ko/Action.csv", "#,Name\r\n7,��\r\n");
        Assert.Throws<ToolException>(() => CsvConverter.Convert(csv, outDir, null, ["Action"])); // UTF-8 아님
        Assert.False(File.Exists(Path.Combine(outDir, "ActionList_Korean.txt")));
    }

    // 서버 목록: 새로 만든 목록에 없는 id(공식 플러그인에만 있는 대만 서버 등)는 남기고 id 순으로
    [Fact]
    public void KeepsIdsOnlyInTheBaseList()
    {
        using var tmp = new TempDir();
        string path = tmp.File("WorldList_English.txt", "21|Ravana\r\n4030|NewName\r\n");
        CsvConverter.KeepMissingIds(path, "4028|伊弗利特\n21|Ravana\n4030|OldName\n");
        Assert.Equal(["21|Ravana", "4028|伊弗利特", "4030|NewName"], File.ReadAllLines(path));
        CsvConverter.KeepMissingIds(path, null); // 베이스를 읽지 못함 -> 그대로
        Assert.Equal(3, File.ReadAllLines(path).Length);
    }
}

public class MachinaOpcodesTests
{
    private const string Base = "﻿ActorCast|196\r\nEffect|2A0\r\nNpcSpawn|1C3\r\n";

    [Fact]
    public void AppliesValuesKeepingFormat()
    {
        var merge = MachinaOpcodes.Apply(Base, new() { ["ActorCast"] = 0x196, ["Effect"] = 0x123 });
        Assert.Equal("﻿ActorCast|196\r\nEffect|123\r\nNpcSpawn|1C3\r\n", merge.Text);
        Assert.Equal(1, merge.Changed);
        Assert.Equal(["NpcSpawn"], merge.Missing); // 출처에 없음 -> 베이스 값 유지 (바뀌지 않은 것으로 세지 않도록 따로 알림)
    }

    [Fact]
    public void ParseRejectsGarbage() =>
        Assert.Throws<ToolException>(() => MachinaOpcodes.Parse("garbage line\r\n", "test"));
}

public class DataminingTests
{
    [Theory]
    [InlineData("7.57 (#120)", "7.57")]
    [InlineData("Patch 7.56", "7.56")]
    [InlineData("Patch 7.45 hotfix", "7.45h")]
    [InlineData("TC Patch 7.25", "7.25")]
    [InlineData("Add patch 7.56 Simplified Chinese chs data", "7.56")]
    [InlineData("Update with latest schemas", null)]
    public void ReadsPatchNumberFromCommitMessage(string message, string? expected) =>
        Assert.Equal(expected, Datamining.Retail(message));
}
