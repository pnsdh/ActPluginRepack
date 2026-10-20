using System.Text.Json;
using System.Xml.Linq;

namespace ActPluginRepack.Tests;

// extras\ 의 OverlayPlugin opcodes.jsonc / Lemegeton blueprint
public class OverlayPluginOutputTests
{
    private const string Version = "2026.10.06.0000.0000";

    private static readonly string Template = TemplateRaw.ReplaceLineEndings("\n");

    private const string TemplateRaw = """
        // 머리 주석 { 괄호 }
        {
            "Global": {
                "2026.09.15.0000.0000": {
                    // Version: 7.56 "따옴표" {
                    "MapEffect": {
                        "opcode": 100,
                        "size": 11
                    },
                    "RSVData": { "opcode": 200, "size": 1080, "url": "https://example.com/{x}" }
                }
            },
            "Korean": {
                "2026.09.15.0000.0000": {
                    "MapEffect": { "opcode": 101, "size": 12 }
                }
            }
        }
        """;

    private static ResolvedOpcodes Resolved(string region, Dictionary<string, int> server) =>
        new(Region.Find(region)!, Version, new OpcodeSet("test src", server, "7.57"));

    private static JsonDocument Parse(string text) =>
        JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

    private static string Write(string template, params ResolvedOpcodes[] resolved)
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "OverlayPlugin", "opcodes.jsonc");
        OverlayPluginOutput.Write(path, template, resolved);
        return File.ReadAllText(path);
    }

    // 원문 블록 ("    \"Global\": {" 부터 그 블록이 닫힐 때까지)
    private static string Block(string text, string region)
    {
        int start = text.IndexOf($"    \"{region}\": {{", StringComparison.Ordinal);
        int end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
        return text[start..(end + "\n    }".Length)];
    }

    [Fact]
    public void UpdatesOneRegionAndKeepsOthersVerbatim()
    {
        string text = Write(Template, Resolved("korean", new() { ["MapEffect"] = 0x1A3 }));
        Assert.StartsWith("// 머리 주석 { 괄호 }\n{", text);
        Assert.Contains(Block(Template, "Global"), text);                  // 주석·따옴표·괄호가 든 원문 그대로
        Assert.Contains("// Version: 7.57 (ActPluginRepack: test src)", text);

        using var doc = Parse(text);
        var korean = doc.RootElement.GetProperty("Korean");
        var entry = Assert.Single(korean.EnumerateObject());               // 옛 버전 항목은 새 버전으로 바뀜
        Assert.Equal(Version, entry.Name);
        Assert.Equal(0x1A3, entry.Value.GetProperty("MapEffect").GetProperty("opcode").GetInt32());
        Assert.Equal(12, entry.Value.GetProperty("MapEffect").GetProperty("size").GetInt32()); // size 는 틀 값
        Assert.Equal(100, doc.RootElement.GetProperty("Global").GetProperty("2026.09.15.0000.0000")
            .GetProperty("MapEffect").GetProperty("opcode").GetInt32());
    }

    [Fact]
    public void NewRegionUsesGlobalShapeAndDropsMissingNames()
    {
        // 틀에 없는 지역은 끝에 추가, 모양(이름 순서·size)은 Global 것. 출처에 없는 이름은 뺀다.
        string text = Write(Template, Resolved("simplified-chinese", new() { ["RSV"] = 0x2B0 }));
        using var doc = Parse(text);
        Assert.Equal(["Global", "Korean", "Chinese"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        var entry = doc.RootElement.GetProperty("Chinese").GetProperty(Version);
        var rsv = entry.GetProperty("RSVData");                            // 표준 이름 RSV -> OverlayPlugin 이름 RSVData
        Assert.Equal(0x2B0, rsv.GetProperty("opcode").GetInt32());
        Assert.Equal(1080, rsv.GetProperty("size").GetInt32());
        Assert.False(entry.TryGetProperty("MapEffect", out _));
    }

    // 출처에 없는 이름: 틀의 같은 지역 항목이 같은 게임 버전이면 그 값으로 채우고, 옛 버전이면 뺀다 (뺀 것은 돌려준다)
    [Fact]
    public void MissingNamesComeFromSameVersionTemplateOnly()
    {
        string same = Template.Replace("\"2026.09.15.0000.0000\": {\n            // Version", $"\"{Version}\": {{\n            // Version");
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "opcodes.jsonc");
        var dropped = OverlayPluginOutput.Write(path, same, [Resolved("global", new() { ["RSV"] = 0x2B0 }), Resolved("korean", new() { ["RSV"] = 0x2B0 })]);
        using var doc = Parse(File.ReadAllText(path));
        Assert.Equal(100, doc.RootElement.GetProperty("Global").GetProperty(Version).GetProperty("MapEffect").GetProperty("opcode").GetInt32());
        Assert.False(doc.RootElement.GetProperty("Korean").GetProperty(Version).TryGetProperty("MapEffect", out _)); // 틀의 한국 항목은 옛 버전
        Assert.Equal(["Korean MapEffect"], dropped);
    }

    [Fact]
    public void EntriesUseStandardNames()
    {
        var entries = OverlayPluginOutput.Entries(Template);
        Assert.Equal(["Global", "Korean"], entries.Select(e => e.Region));
        Assert.Equal("2026.09.15.0000.0000", entries[0].Version);
        Assert.Equal(new Dictionary<string, int> { ["MapEffect"] = 100, ["RSV"] = 200 }, entries[0].Server); // RSVData -> RSV
        Assert.Empty(OverlayPluginOutput.Entries("{ 깨짐"));
    }

    [Fact]
    public void FollowsTemplateLineEnding()
    {
        string text = Write(Template.Replace("\n", "\r\n"), Resolved("korean", new() { ["MapEffect"] = 1 }));
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        using var _ = Parse(text);
    }

    [Fact]
    public void NothingResolvedWritesNothing()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "opcodes.jsonc");
        OverlayPluginOutput.Write(path, Template, [new ResolvedOpcodes(Region.Find("korean")!, null, new OpcodeSet("x", []))]);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TemplateWithoutObjectIsRejected() =>
        Assert.Throws<ToolException>(() => Write("// 비어 있음\n", Resolved("korean", new() { ["MapEffect"] = 1 })));

    [Fact]
    public void CommentAboveRegionIsKept()
    {
        string template = Template.Replace("    \"Korean\": {", "    // 한국 서버는 별도\n    \"Korean\": {");
        string text = Write(template, Resolved("simplified-chinese", new() { ["MapEffect"] = 1 }));
        Assert.Contains("// 한국 서버는 별도", text);
    }

    // 아래 둘은 문자열 안의 \" 와 /* */ 주석. 예전 SliceTopLevel 은 이것을 몰라 끝나지 않았다(고침) —
    // 다시 그렇게 되면 시험이 멈추지 않고 실패하게 시간 제한을 둔다.
    private static string WriteWithTimeout(string template, params ResolvedOpcodes[] resolved)
    {
        var run = Task.Run(() => Write(template, resolved));
        Assert.True(run.Wait(TimeSpan.FromSeconds(5)), "SliceTopLevel 이 끝나지 않음");
        return run.Result;
    }

    [Fact]
    public void EscapedQuoteInStringIsHandled()
    {
        string template = Template.Replace("\"https://example.com/{x}\"", "\"say \\\"{\\\" or \\\"\"");
        string text = WriteWithTimeout(template, Resolved("korean", new() { ["MapEffect"] = 1 }));
        Assert.Contains("\"url\": \"say \\\"{\\\" or \\\"\"", text);
        using var doc = Parse(text);
        Assert.Equal(["Global", "Korean"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void BlockCommentIsHandled()
    {
        string template = Template.Replace("// Version: 7.56 \"따옴표\" {", "/* \"Old\": { 옛 값 */");
        string text = WriteWithTimeout(template, Resolved("korean", new() { ["MapEffect"] = 1 }));
        Assert.Contains("/* \"Old\": { 옛 값 */", text);
        using var doc = Parse(text);
        Assert.Equal(["Global", "Korean"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }
}

public class LemegetonOutputTests
{
    private static ResolvedOpcodes Resolved(string region, string? version, Dictionary<string, int> server) =>
        new(Region.Find(region)!, version, new OpcodeSet("test", server));

    [Fact]
    public void WritesRegionsInFixedOrderAndSkipsMissing()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "extras", "lemegeton.xml");
        LemegetonOutput.Write(path, [
            Resolved("korean", "2026.10.06.0000.0000", new() { ["ActorCast"] = 0x1A0 }),
            Resolved("traditional-chinese", "2026.10.01.0000.0000", new() { ["ActorCast"] = 1 }), // Lemegeton 지역 아님
            Resolved("simplified-chinese", null, new() { ["ActorCast"] = 2 }),                  // 버전 모름
            Resolved("global", "2026.10.06.0000.0000", new() { ["ActorCast"] = 0x1A1, ["ActionEffect1"] = 0x2A0 }),
        ]);
        var doc = XDocument.Load(path);
        var regions = doc.Root!.Element("Regions")!.Elements("Region").ToList();
        Assert.Equal(["EN/DE/FR/JP", "KR"], regions.Select(r => (string)r.Attribute("Name")!));
        Assert.Equal("2026.10.06.0000.0000", (string)regions[0].Attribute("Version")!);
        var global = regions[0].Element("Opcodes")!.Elements("Opcode").ToDictionary(o => (string)o.Attribute("Name")!, o => (int)o.Attribute("Id")!);
        Assert.Equal(new Dictionary<string, int> { ["Ability1"] = 0x2A0, ["ActorCast"] = 0x1A1 }, global); // 10진수, 출력 이름
        Assert.Single(regions[1].Element("Opcodes")!.Elements("Opcode"));
        Assert.DoesNotContain("\n", File.ReadAllText(path).Replace("\r\n", ""));
    }

    [Fact]
    public void NoRegionWritesNothing()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "lemegeton.xml");
        LemegetonOutput.Write(path, [Resolved("traditional-chinese", "2026.10.01.0000.0000", new() { ["ActorCast"] = 1 })]);
        Assert.False(File.Exists(path));
    }
}
