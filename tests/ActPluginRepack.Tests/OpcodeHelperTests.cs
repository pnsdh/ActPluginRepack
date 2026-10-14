namespace ActPluginRepack.Tests;

// opcodediff 대응표 / Machina opcode 원문 고치기 / 배포 설명의 링크
public class OpcodeDiffTests
{
    [Fact]
    public void UsesOnlyOneToOneEntries()
    {
        var map = OpcodeSources.ParseDiff("""
            [
              { "old": ["0x346"], "new": ["0x89"] },
              { "old": ["0x100", "0x101"], "new": ["0x200"] },
              { "old": ["0x300"], "new": ["0x400", "0x401"] },
              { "old": [], "new": ["0x500"] },
              { "old": ["1C3"], "new": ["0X1d4"] }
            ]
            """, "test");
        Assert.Equal(new Dictionary<int, int> { [0x346] = 0x89, [0x1C3] = 0x1D4 }, map);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "old": ["0x1"], "new": ["0x2"] }""")]   // 배열이 아님
    [InlineData("""[ { "old": ["0x1"] } ]""")]                // new 없음
    [InlineData("""[ { "old": ["zz"], "new": ["0x2"] } ]""")] // 16진수 아님
    [InlineData("""[ { "old": [1], "new": ["0x2"] } ]""")]    // 문자열 아님
    public void MalformedInputIsToolException(string json)
    {
        var ex = Assert.Throws<ToolException>(() => OpcodeSources.ParseDiff(json, "opcodediff 7.57"));
        Assert.Contains("opcodediff 7.57", ex.Message);
    }

    [Fact]
    public void HugeValueIsToolException() =>
        Assert.Throws<ToolException>(() => OpcodeSources.ParseDiff("""[ { "old": ["0x1FFFFFFFF"], "new": ["0x2"] } ]""", "test"));

    [Fact]
    public void NullValueIsToolException() =>
        Assert.Throws<ToolException>(() => OpcodeSources.ParseDiff("""[ { "old": [null], "new": ["0x2"] } ]""", "test"));
}

public class MachinaOpcodesApplyTests
{
    [Fact]
    public void KeepsLfAndCountsOnlyRealChanges()
    {
        var merge = MachinaOpcodes.Apply("ActorCast|1c3\nEffect|2A0\n\nNpcSpawn|100", new()
        {
            ["ActorCast"] = 0x1C3,   // 대소문자만 다른 같은 값 -> 바뀐 것 아님 (표기는 대문자로)
            ["Effect"] = 0x2A1,
            ["NpcSpawn"] = 0x101,
            ["Unused"] = 0x999,      // 베이스에 없는 이름은 넣지 않는다
        });
        Assert.Equal("ActorCast|1C3\nEffect|2A1\n\nNpcSpawn|101", merge.Text);
        Assert.Equal(2, merge.Changed);
        Assert.Empty(merge.Missing);
    }

    [Fact]
    public void BomOnFirstLineIsKeptAndNameStillMatches()
    {
        var merge = MachinaOpcodes.Apply("﻿ActorCast|196\r\nEffect|2A0\r\n", new() { ["ActorCast"] = 0x197 });
        Assert.Equal("﻿ActorCast|197\r\nEffect|2A0\r\n", merge.Text);
        Assert.Equal(1, merge.Changed);
        Assert.Equal(["Effect"], merge.Missing);
    }

    [Fact]
    public void ParseReadsWhatApplyWrites()
    {
        var merge = MachinaOpcodes.Apply("﻿A|1\r\nB|2\r\n", new() { ["A"] = 0xABC, ["B"] = 0 });
        Assert.Equal(new Dictionary<string, int> { ["A"] = 0xABC, ["B"] = 0 }, MachinaOpcodes.Parse(merge.Text, "test"));
    }
}

public class HotfixLinkTests
{
    [Fact]
    public void PatchTagOfNothingIsNull() =>
        Assert.Null(HotfixPackage.PatchTag(new Dictionary<string, string>())); // 대응표(네트워크)를 읽지 않음

    [Theory]
    [InlineData("IDA korean 2026.10.06", "IDA 직접 추출 ([finder](https://github.com/Yarukon/FFXIVNetworkOpcodes))")]
    [InlineData("Yarukon", "Yarukon")]                                         // 폴더 없으면 링크 없이
    [InlineData("opcodediff", "opcodediff")]
    [InlineData("수동 입력", "수동 입력")]                                      // 모르는 출처는 그대로
    [InlineData("수동 입력 +보충", "수동 입력 (빠진 항목은 다른 출처로 보충)")]
    [InlineData("Yarukon CN_2026 10", "[Yarukon CN_2026](https://github.com/Yarukon/FFXIVNetworkOpcodes/tree/master/output/CN_2026)")]
    public void OpcodeSourceLinkOtherForms(string source, string expected) =>
        Assert.Equal(expected, HotfixPackage.OpcodeSourceLink(source));

    [Theory]
    [InlineData("deucalion main a7316e7f00 (2026-10-13) - korean 3", "[deucalion `a7316e7f00`](https://github.com/ff14wed/deucalion/commit/a7316e7f00)")]
    [InlineData("deucalion main xyz", "deucalion main xyz")]                   // 커밋 해시가 아니면 그대로
    [InlineData("직접 고친 값", "직접 고친 값")]
    [InlineData("deucalion release 1.5.1+x", "[deucalion 1.5.1+x](https://github.com/ff14wed/deucalion/releases/tag/1.5.1%2Bx)")]
    public void SignatureSourceLinkOtherForms(string comment, string expected) =>
        Assert.Equal(expected, HotfixPackage.SignatureSourceLink(comment));
}
