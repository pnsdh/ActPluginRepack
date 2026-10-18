using System.Text;
using System.Text.Json;

// 플러그인 밖에서 쓰는 opcode 파일들 (extras\ 에 출력).
internal sealed record ResolvedOpcodes(Region Region, string? GameVersion, OpcodeSet Set);

// Lemegeton blueprint — Yarukon ffxiv_opcode_finder / update_lemegeton.py 와 같은 형식.
internal static class LemegetonOutput
{
    // 쓸 지역(Lemegeton 대상, 게임 버전을 앎)이 없으면 쓰지 않고 false
    public static bool Write(string path, IEnumerable<ResolvedOpcodes> resolved)
    {
        var regions = resolved.Where(r => r.Region.Lemegeton != null && r.GameVersion != null)
            .OrderBy(r => Array.IndexOf(Region.All, r.Region)).ToList(); // Global, CN, KR
        if (regions.Count == 0) return false;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Blueprint>\r\n\t<Regions>\r\n");
        foreach (var r in regions)
        {
            sb.Append($"\t\t<Region Name=\"{r.Region.Lemegeton}\" Version=\"{r.GameVersion}\">\r\n\t\t\t<Opcodes>\r\n");
            var missing = new List<string>();
            foreach (var (outName, name) in OpcodeNames.Lemegeton)
            {
                if (r.Set.Server.TryGetValue(name, out int op)) sb.Append($"\t\t\t\t<Opcode Name=\"{outName}\" Id=\"{op}\" />\r\n");
                else missing.Add(outName);
            }
            sb.Append("\t\t\t</Opcodes>\r\n\t\t</Region>\r\n");
            if (missing.Count > 0) Log.Warn($"[lemegeton] {r.Region.Lemegeton}: 값이 없어 뺀 항목 - {string.Join(", ", missing)}");
        }
        sb.Append("\t</Regions>\r\n</Blueprint>\r\n");

        Files.WriteAtomic(path, sb.ToString());
        Log.Info($"[lemegeton] {path} ({string.Join(", ", regions.Select(r => r.Region.Lemegeton))})");
        return true;
    }
}

// OverlayPlugin resources\opcodes.jsonc
//   { "<지역>": { "<게임 버전>": { "<이름>": { "opcode": n, "size": n }, ... } }, ... }
// OverlayPlugin 저장소의 파일을 틀로 쓴다: 갱신하는 지역은 틀의 이름 순서·size 를 그대로 두고 opcode 만 바꿔 새로 쓰고,
// 나머지 지역은 원문 텍스트(주석 포함)를 그대로 둔다. size 는 패치마다 거의 안 바뀌므로 틀 값을 쓴다.
internal static class OverlayPluginOutput
{
    public const string UpstreamUrl =
        "https://raw.githubusercontent.com/OverlayPlugin/OverlayPlugin/main/OverlayPlugin.Core/resources/opcodes.jsonc";

    public static void Write(string path, string template, IEnumerable<ResolvedOpcodes> resolved)
    {
        var updates = resolved.Where(r => r.GameVersion != null)
            .ToDictionary(r => r.Region.Opcodes, StringComparer.Ordinal);
        if (updates.Count == 0) return;

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
            parts.Add(Block(r, shape, nl));
        }

        string text = header + "{" + nl + string.Join("," + nl, parts) + nl + "}" + nl;
        Files.WriteAtomic(path, text);
        Log.Info($"[overlayplugin] {path} ({string.Join(", ", updates.Keys)} 갱신)");
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

    private static string Block(ResolvedOpcodes r, List<(string Name, int Size)> shape, string nl)
    {
        var byOut = OpcodeNames.OverlayPlugin.ToDictionary(m => m.Out, m => m.Name, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append($"    \"{r.Region.Opcodes}\": {{{nl}        \"{r.GameVersion}\": {{{nl}");
        string label = r.Set.PatchLabel != null ? $"Version: {r.Set.PatchLabel} " : "";
        sb.Append($"            // {label}(ActPluginRepack: {r.Set.Source}){nl}");
        var entries = new List<string>();
        var missing = new List<string>();
        foreach (var (name, size) in shape)
        {
            string std = byOut.GetValueOrDefault(name, name);
            if (!r.Set.Server.TryGetValue(std, out int op)) { missing.Add(name); continue; }
            entries.Add($"            \"{name}\": {{{nl}                \"opcode\": {op},{nl}                \"size\": {size}{nl}            }}");
        }
        sb.Append(string.Join("," + nl, entries)).Append($"{nl}        }}{nl}    }}");
        if (missing.Count > 0) Log.Warn($"[overlayplugin] {r.Region.Opcodes}: 값이 없어 뺀 항목 - {string.Join(", ", missing)}");
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
