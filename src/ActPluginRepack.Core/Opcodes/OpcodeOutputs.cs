using System.Text;
using System.Text.Json;

// 플러그인 밖에서 쓰는 opcode 파일들 (extras\ 에 출력).
internal sealed record ResolvedOpcodes(Region Region, string? GameVersion, OpcodeSet Set);

// Lemegeton blueprint — Lemegeton 본가 blueprint.xml 과 같은 형식 (Yarukon update_lemegeton.py 와도 같다).
// 항목(이름·순서)은 본가 blueprint 를 틀로 따른다 — 본가가 항목을 늘려도 놓치지 않게 (BossStatusEffectList 가 그렇게 빠졌었다).
// 틀을 받지 못하거나 읽지 못하면 OpcodeNames.Lemegeton 으로.
internal static class LemegetonOutput
{
    public const string UpstreamUrl = "https://raw.githubusercontent.com/paissaheavyindustries/Resources/main/Blueprint/blueprint.xml";

    // 틀의 지역마다: Region Name, 게임 버전, 표준 이름 -> opcode (본가가 비워 둔 0 은 뺀다). diff 대응표로 바꿀 옛 목록으로도 쓴다.
    // 읽지 못하면 빈 목록.
    public static List<(string Region, string Version, Dictionary<string, int> Server)> Entries(string? template) =>
        Parse(template).Select(r => (r.Region, r.Version, r.Opcodes.Where(o => o.Id != 0)
            .GroupBy(o => StandardName(o.Name)).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal))).ToList();

    // 틀의 항목 이름 (출력 이름, 지역들에 나온 순서대로). 읽지 못하면 null
    public static List<string>? Names(string? template)
    {
        var names = Parse(template).SelectMany(r => r.Opcodes.Select(o => o.Name)).Distinct(StringComparer.Ordinal).ToList();
        return names.Count > 0 ? names : null;
    }

    private static string StandardName(string outName) =>
        OpcodeNames.Lemegeton.FirstOrDefault(m => m.Out == outName).Name ?? outName;

    private static List<(string Region, string Version, List<(string Name, int Id)> Opcodes)> Parse(string? template)
    {
        var result = new List<(string, string, List<(string, int)>)>();
        if (string.IsNullOrWhiteSpace(template)) return result;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(template.TrimStart('﻿'));
            foreach (var region in doc.Descendants("Region"))
            {
                var ops = region.Descendants("Opcode")
                    .Select(o => ((string?)o.Attribute("Name"), int.TryParse((string?)o.Attribute("Id"), out int id) ? id : -1))
                    .Where(o => !string.IsNullOrEmpty(o.Item1) && o.Item2 >= 0).Select(o => (o.Item1!, o.Item2)).ToList();
                if ((string?)region.Attribute("Name") is { } name && (string?)region.Attribute("Version") is { } version)
                    result.Add((name, version, ops));
            }
        }
        catch (System.Xml.XmlException) { }
        return result;
    }

    // 쓸 지역(Lemegeton 대상, 게임 버전을 앎)이 없으면 쓰지 않고 null. 쓰면 값을 구하지 못해 뺀 항목 ("지역 이름", 출처 보고용)
    public static List<string>? Write(string path, IEnumerable<ResolvedOpcodes> resolved, string? template = null)
    {
        var regions = resolved.Where(r => r.Region.Lemegeton != null && r.GameVersion != null)
            .OrderBy(r => Array.IndexOf(Region.All, r.Region)).ToList(); // Global, CN, KR
        if (regions.Count == 0) return null;
        var names = Names(template);
        if (names == null) Log.Note("[lemegeton] 본가 blueprint 를 쓰지 못해 이 도구의 항목 목록으로 씀");
        var items = names?.Select(n => (Out: n, Name: StandardName(n))).ToList() ?? [.. OpcodeNames.Lemegeton];
        var dropped = new List<string>();
        var entries = Entries(template);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Blueprint>\r\n\t<Regions>\r\n");
        foreach (var r in regions)
        {
            sb.Append($"\t\t<Region Name=\"{r.Region.Lemegeton}\" Version=\"{r.GameVersion}\">\r\n\t\t\t<Opcodes>\r\n");
            var missing = new List<string>();
            // 출처에 없는 이름은 틀의 같은 지역 항목이 같은 게임 버전이면 그 값으로 (본가가 비워 둔 0 은 Entries 가 뺐다)
            var same = entries.FirstOrDefault(e => e.Region == r.Region.Lemegeton && e.Version == r.GameVersion).Server;
            foreach (var (outName, name) in items)
            {
                if (r.Set.Server.TryGetValue(name, out int op) || same != null && same.TryGetValue(name, out op))
                    sb.Append($"\t\t\t\t<Opcode Name=\"{outName}\" Id=\"{op}\" />\r\n");
                else missing.Add(outName);
            }
            sb.Append("\t\t\t</Opcodes>\r\n\t\t</Region>\r\n");
            if (missing.Count > 0) Log.Warn($"[lemegeton] {r.Region.Lemegeton}: 값이 없어 뺀 항목 - {string.Join(", ", missing)}");
            dropped.AddRange(missing.Select(m => $"{r.Region.Lemegeton} {m}"));
        }
        sb.Append("\t</Regions>\r\n</Blueprint>\r\n");

        Files.WriteAtomic(path, sb.ToString());
        Log.Info($"[lemegeton] {path} ({string.Join(", ", regions.Select(r => r.Region.Lemegeton))})");
        return dropped;
    }
}

// OverlayPlugin resources\opcodes.jsonc
//   { "<지역>": { "<게임 버전>": { "<이름>": { "opcode": n, "size": n }, ... } }, ... }
// OverlayPlugin 저장소의 파일을 틀로 쓴다: 갱신하는 지역은 틀의 이름 순서·size 를 그대로 두고 opcode 만 바꿔 새로 쓰고,
// 나머지 지역은 원문 텍스트(주석 포함)를 그대로 둔다. size 는 패치마다 거의 안 바뀌므로 틀 값을 쓴다.
// 출처에 없는 이름은 틀의 같은 지역 항목이 같은 게임 버전이면(OverlayPlugin 이 이미 갱신함) 그 값으로, 아니면 뺀다 (옛 값은 틀림).
// 형식은 본가 그대로 — 포크에 먼저 넣은 뒤 본가가 같은 값을 올리면 git 이 충돌 없이 합치도록 (사용자 결정, 2026-10-21):
//   - 새로 쓰는 블록의 주석은 본가처럼 "// Version: <패치 라벨>" 만 (출처는 로그에)
//   - 틀의 같은 지역 항목이 같은 게임 버전이고 값도 모두 같으면 그 블록은 원문 그대로 (주석까지 바이트 단위로)
internal static class OverlayPluginOutput
{
    public const string UpstreamUrl =
        "https://raw.githubusercontent.com/OverlayPlugin/OverlayPlugin/main/OverlayPlugin.Core/resources/opcodes.jsonc";

    // 틀의 지역마다 (첫) 버전 항목: 지역 이름, 게임 버전, 표준 이름 -> opcode. 같은 버전일 때 빠진 이름을 채우고, 옛 버전이면
    // diff 대응표로 바꿀 옛 목록이 된다 (패치 당일에는 보통 직전 출시 값). 형식이 이상하면 빈 목록 (Write 가 따로 알린다).
    public static List<(string Region, string Version, Dictionary<string, int> Server)> Entries(string template)
    {
        var result = new List<(string, string, Dictionary<string, int>)>();
        var byOut = OpcodeNames.OverlayPlugin.ToDictionary(m => m.Out, m => m.Name, StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(template, JsonOptions.Comments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var region in doc.RootElement.EnumerateObject())
            {
                if (region.Value.ValueKind != JsonValueKind.Object) continue;
                var first = region.Value.EnumerateObject().FirstOrDefault();
                if (first.Value.ValueKind != JsonValueKind.Object) continue;
                var server = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in first.Value.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("opcode", out var op) && op.TryGetInt32(out int v))
                        server[byOut.GetValueOrDefault(p.Name, p.Name)] = v;
                result.Add((region.Name, first.Name, server));
            }
        }
        catch (JsonException) { }
        return result;
    }

    // 돌려주는 것: 값을 구하지 못해 뺀 항목 ("지역 이름", 출처 보고용)
    public static List<string> Write(string path, string template, IEnumerable<ResolvedOpcodes> resolved)
    {
        var dropped = new List<string>();
        var updates = resolved.Where(r => r.GameVersion != null)
            .ToDictionary(r => r.Region.Opcodes, StringComparer.Ordinal);
        if (updates.Count == 0) return dropped;
        var entries = Entries(template);

        string nl = template.Contains("\r\n") ? "\r\n" : "\n"; // 틀의 줄바꿈을 따른다
        int open = FirstBraceOutsideComment(template);
        string header = template[..open];
        var blocks = SliceTopLevel(template, open); // 지역 이름 -> 원문 블록 ("    \"Global\": {...}")
        using var doc = JsonDocument.Parse(template, JsonOptions.Comments);

        var parts = new List<string>();
        var regionNames = blocks.Select(b => b.Name).Concat(updates.Keys.Where(k => blocks.All(b => b.Name != k)));
        foreach (string region in regionNames)
        {
            if (!updates.TryGetValue(region, out var r))
            {
                parts.Add(blocks.First(b => b.Name == region).Text);
                continue;
            }
            // 틀: 같은 지역의 (첫) 버전 항목, 없으면 Global
            var shape = Shape(doc.RootElement, region) ?? Shape(doc.RootElement, "Global")
                        ?? throw new ToolException("OverlayPlugin opcodes.jsonc 틀에 Global 항목이 없습니다.");
            // 틀의 같은 지역 항목이 이번 게임 버전이면 빠진 이름을 그 값으로 (다른 지역 항목은 opcode 가 다를 수 있어 쓰지 않는다)
            var same = entries.FirstOrDefault(e => e.Region == region && e.Version == r.GameVersion).Server;
            if (same != null && blocks.FirstOrDefault(b => b.Name == region).Text is { } original && SameValues(r, shape, same))
            {
                Log.Note($"[overlayplugin] {region}: 원본이 이미 같은 게임 버전·같은 값이라 그대로 ({r.Set.Source})");
                parts.Add(original);
                continue;
            }
            Log.Info($"[overlayplugin] {region} {r.GameVersion} <- {r.Set.Source}" + (r.Set.PatchLabel != null ? $" (Version: {r.Set.PatchLabel})" : ""));
            parts.Add(Block(r, shape, same, nl, dropped));
        }

        string text = header + "{" + nl + string.Join("," + nl, parts) + nl + "}" + nl;
        Files.WriteAtomic(path, text);
        Log.Info($"[overlayplugin] {path}");
        return dropped;
    }

    // 쓸 값(출처 값, 없으면 원본 값)이 틀의 이름마다 원본과 모두 같은지
    private static bool SameValues(ResolvedOpcodes r, List<(string Name, int Size)> shape, Dictionary<string, int> same)
    {
        var byOut = OpcodeNames.OverlayPlugin.ToDictionary(m => m.Out, m => m.Name, StringComparer.Ordinal);
        return shape.All(s =>
        {
            string std = byOut.GetValueOrDefault(s.Name, s.Name);
            return same.TryGetValue(std, out int old) && (!r.Set.Server.TryGetValue(std, out int now) || now == old);
        });
    }

    private static List<(string Name, int Size)>? Shape(JsonElement root, string region)
    {
        if (!root.TryGetProperty(region, out var versions)) return null;
        var first = versions.EnumerateObject().FirstOrDefault();
        if (first.Value.ValueKind != JsonValueKind.Object) return null;
        return first.Value.EnumerateObject()
            .Select(p => (p.Name, p.Value.TryGetProperty("size", out var s) ? s.GetInt32() : 0))
            .ToList();
    }

    private static string Block(ResolvedOpcodes r, List<(string Name, int Size)> shape, Dictionary<string, int>? same, string nl,
                                List<string> dropped)
    {
        var byOut = OpcodeNames.OverlayPlugin.ToDictionary(m => m.Out, m => m.Name, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append($"    \"{r.Region.Opcodes}\": {{{nl}        \"{r.GameVersion}\": {{{nl}");
        // 본가 형식: "// Version: 7.57h" (라벨을 모르면 주석 줄 없이)
        if (r.Set.PatchLabel != null) sb.Append($"            // Version: {r.Set.PatchLabel}{nl}");
        var entries = new List<string>();
        var missing = new List<string>();
        var kept = new List<string>();
        foreach (var (name, size) in shape)
        {
            string std = byOut.GetValueOrDefault(name, name);
            if (!r.Set.Server.TryGetValue(std, out int op))
            {
                if (same == null || !same.TryGetValue(std, out op)) { missing.Add(name); continue; }
                kept.Add(name);
            }
            entries.Add($"            \"{name}\": {{{nl}                \"opcode\": {op},{nl}                \"size\": {size}{nl}            }}");
        }
        sb.Append(string.Join("," + nl, entries)).Append($"{nl}        }}{nl}    }}");
        if (kept.Count > 0) Log.Note($"[overlayplugin] {r.Region.Opcodes}: 출처에 없어 원본(같은 게임 버전)의 값을 씀 - {string.Join(", ", kept)}");
        if (missing.Count > 0) Log.Warn($"[overlayplugin] {r.Region.Opcodes}: 값이 없어 뺀 항목 - {string.Join(", ", missing)}");
        dropped.AddRange(missing.Select(m => $"{r.Region.Opcodes} {m}"));
        return sb.ToString();
    }

    // 최상위 객체의 각 멤버 원문 (그 멤버 위의 주석 줄 포함). 주석(// , /* */)과 문자열(\" 이스케이프 포함) 안의 괄호는 건너뛴다.
    // 틀은 저장소에서 받아 오므로 형식이 이상해도 끝나야 한다 — 닫히지 않은 문자열·주석은 ToolException.
    private static List<(string Name, string Text)> SliceTopLevel(string s, int open)
    {
        var result = new List<(string, string)>();
        int i = open + 1, depth = 0, start = LineAfter(s, open + 1);
        string? name = null;
        while (i < s.Length)
        {
            if (SkipCommentOrString(s, i) is int next and >= 0)
            {
                if (s[i] == '"' && depth == 0 && name == null) name = s[(i + 1)..(next - 1)];
                i = next;
                continue;
            }
            char c = s[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                if (depth == 0) break; // 최상위 닫힘
                if (--depth == 0 && name != null)
                {
                    result.Add((name, s[start..(i + 1)].TrimStart('\r', '\n')));
                    name = null;
                    // 다음 멤버는 쉼표 다음 줄부터 (그 위의 주석 줄도 그 멤버의 것)
                    int comma = s.IndexOf(',', i + 1);
                    start = comma >= 0 ? LineAfter(s, comma + 1) : i + 1;
                }
            }
            i++;
        }
        return result;
    }

    // s[i] 에서 주석이나 문자열이 시작하면 그 끝 다음 위치, 아니면 -1
    private static int SkipCommentOrString(string s, int i)
    {
        if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            return s.IndexOf('\n', i) is int n and >= 0 ? n + 1 : s.Length;
        if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            return s.IndexOf("*/", i + 2, StringComparison.Ordinal) is int e and >= 0 ? e + 2
                : throw new ToolException("OverlayPlugin opcodes.jsonc 틀에 닫히지 않은 주석이 있습니다.");
        if (s[i] != '"') return -1;
        for (int k = i + 1; k < s.Length; k++)
        {
            if (s[k] == '\\') { k++; continue; } // \" 등 이스케이프
            if (s[k] == '"') return k + 1;
        }
        throw new ToolException("OverlayPlugin opcodes.jsonc 틀에 닫히지 않은 문자열이 있습니다.");
    }

    // i 이후 첫 줄바꿈 다음 위치 (같은 줄에 내용이 더 있으면 i 그대로)
    private static int LineAfter(string s, int i)
    {
        int k = i;
        while (k < s.Length && (s[k] == ' ' || s[k] == '\t' || s[k] == '\r')) k++;
        return k < s.Length && s[k] == '\n' ? k + 1 : i;
    }

    private static int FirstBraceOutsideComment(string s)
    {
        for (int i = 0; i < s.Length; )
        {
            if (SkipCommentOrString(s, i) is int next and >= 0) { i = next; continue; }
            if (s[i] == '{') return i;
            i++;
        }
        throw new ToolException("OverlayPlugin opcodes.jsonc 틀에 최상위 객체가 없습니다.");
    }
}
