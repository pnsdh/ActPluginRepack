namespace ActPluginRepack.Tests;

// 플러그인 쪽 시그니처(memory / oodle) 표기 변환과 리소스 원문 고치기
public class PluginSignatureTests
{
    [Theory]
    [InlineData("48 8B DA ? 4C", "488bda**4c")]
    [InlineData("48 8b ** 4c", "488b**4c")]        // Machina 표기 ** 도 1바이트
    [InlineData("48 ?? 8B", "48****8b")]            // pelite 의 ?? 는 2바이트
    [InlineData("488BDA?4C", "488bda**4c")]         // 공백 없어도 됨
    public void ToMemoryFormat(string pattern, string expected) =>
        Assert.Equal(expected, PluginSignatures.ToMemoryFormat(pattern));

    [Theory]
    [InlineData("48 83 7B ? 00", "48 83 7b ** 00")]
    [InlineData("48 ?? 7b", "48 ** ** 7b")]
    [InlineData("48 ** 7b", "48 ** 7b")]
    public void ToOodleFormat(string pattern, string expected) =>
        Assert.Equal(expected, PluginSignatures.ToOodleFormat(pattern));

    [Theory]
    [InlineData("E8 $ { ' } 4C")]      // 점프 따라가기
    [InlineData("48 (8B | 8D) 05")]   // 대안
    [InlineData("48 [2-4] 05")]       // 건너뛰기
    [InlineData("48 8")]              // 짝 없는 16진수
    public void FormatsRejectWhatPluginCannotRead(string pattern)
    {
        Assert.Throws<ToolException>(() => PluginSignatures.ToMemoryFormat(pattern));
        Assert.Throws<ToolException>(() => PluginSignatures.ToOodleFormat(pattern));
    }

    [Fact]
    public void ReadParsesBothFormatsAndRoundTrips()
    {
        // Machina 문자열은 "**", "??", "?" 모두 1바이트 와일드카드
        using var plugin = FakeAssembly.Plugin(
            "﻿ChatLog|488BDA**4c\r\nZoneId|  8b0d????  \r\nbroken line\r\n",
            [("OodleInit", "48 83 7B ** ?? ? 75 0A")],
            [("OodleInit", "48 83 7B ** 75")]);
        var sigs = PluginSignatures.Read(plugin);

        var chat = sigs.Single(s => s is { Variant: "Global", Name: "ChatLog" });
        Assert.Equal("memory", chat.Kind);
        Assert.Equal("48 8b da ? 4c", chat.Pattern);
        Assert.Equal("488bda**4c", PluginSignatures.ToMemoryFormat(chat.Pattern));
        Assert.Equal("8b 0d ? ? ? ?", sigs.Single(s => s is { Variant: "Global", Name: "ZoneId" }).Pattern); // 앞뒤 공백 무시, ? 하나가 1바이트
        Assert.DoesNotContain(sigs, s => s.Name == "broken line");

        var oodle = sigs.Single(s => s is { Kind: "oodle", Variant: "Global" });
        Assert.Equal("OodleInit", oodle.Name);
        Assert.Equal("48 83 7b ? ? ? 75 0a", oodle.Pattern);
        Assert.Equal("48 83 7b ** ** ** 75 0a", PluginSignatures.ToOodleFormat(oodle.Pattern));
        Assert.Equal("48 83 7b ? 75", sigs.Single(s => s is { Kind: "oodle", Variant: "Korean" }).Pattern);
    }

    [Fact]
    public void ApplyMemoryKeepsBomLineEndsAndOrder()
    {
        const string text = "﻿ChatLog|488bda**\r\nZoneId|8b0d\r\n# comment|x\nLast|11\r\n";
        var (result, unknown) = PluginSignatures.ApplyMemory(text, new()
        {
            ["ChatLog"] = "48 8B ? 4C",  // BOM 이 붙은 첫 줄도 이름으로 찾는다
            ["Last"] = "22 ?? 33",
            ["Nope"] = "11 22",
        });
        Assert.Equal("﻿ChatLog|488b**4c\r\nZoneId|8b0d\r\n# comment|x\nLast|22****33\r\n", result);
        Assert.Equal(["Nope"], unknown);
    }

    [Fact]
    public void ApplyMemoryWithoutTrailingNewline()
    {
        var (result, unknown) = PluginSignatures.ApplyMemory("A|11\nB|22", new() { ["B"] = "33 44" });
        Assert.Equal("A|11\nB|3344", result);
        Assert.Empty(unknown);
    }

    [Fact]
    public void OodlePatchRejectsUnknownName()
    {
        using var plugin = FakeAssembly.Plugin("ChatLog|11\r\n", [("OodleInit", "48 83")], [("OodleInit", "48 83")]);
        using var machina = Costura.ReadInner(plugin, Costura.Machina);
        var ex = Assert.Throws<ToolException>(() =>
            PluginSignatures.PatchOodle(machina, [new Deucalion.Sig("OodleNope", "11 22 33 44")]));
        Assert.Contains("OodleInit", ex.Message); // 쓸 수 있는 이름을 알려 준다

        PluginSignatures.PatchOodle(machina, [new Deucalion.Sig("Korean.OodleInit", "48 83 ? 75")]);
        Assert.Equal("48 83 ** 75", (string)PluginSignatures.OodleLoads(machina, "Korean").Single().Load.Operand);
        Assert.Equal("48 83", (string)PluginSignatures.OodleLoads(machina, "Global").Single().Load.Operand); // 다른 변형은 그대로
    }
}

// 리소스를 바꿔도 순서는 그대로 — 플러그인은 override 파일을 리소스 순서대로 읽고 겹치는 키는 나중 것이 이긴다
public class ResourceOrderTests
{
    [Fact]
    public void ReplacingAResourceKeepsItsPlace()
    {
        using var asm = FakeAssembly.Create("Order", new Version(1, 0));
        var module = asm.MainModule;
        foreach (string n in new[] { "a", "b", "c" })
            module.Resources.Add(new Mono.Cecil.EmbeddedResource(n, Mono.Cecil.ManifestResourceAttributes.Public, [1]));
        var b = module.Resources.Single(r => r.Name == "b");
        Costura.ReplaceInPlace(module, b, new Mono.Cecil.EmbeddedResource("b", b.Attributes, [2]));
        Assert.Equal(["a", "b", "c"], module.Resources.Select(r => r.Name));
        Assert.Equal([2], ((Mono.Cecil.EmbeddedResource)module.Resources[1]).GetResourceData());
    }
}
