using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// deucalion 말고 플러그인 쪽에 있는 게임 exe 시그니처 (패치로 깨질 수 있는 것).
//   memory  FFXIV_ACT_Plugin.Memory.dll 리소스 Signatures_<Global|TraditionalChinese>.txt ("이름|488bda**..")
//           — 파티/몹/타깃/채팅/존 등 게임 메모리 읽기. 플러그인은 첫 번째 일치를 쓴다.
//   oodle   Machina.FFXIV.dll 의 SigScan(글로벌) / KoreanSigScan(한국) .Signatures ("48 83 7b ** ..")
//           — 패킷 압축 해제(Oodle) 함수. deucalion 이 아닌 소켓/WinPcap 캡처에서 쓴다.
// 시그니처는 내부적으로 SigPattern 표기("48 8b da ?")로 다루고, 쓸 때 원래 형식으로 되돌린다.
internal sealed record GameSig(string Kind, string Variant, string Name, string Pattern);

internal static class PluginSignatures
{
    public const string MemoryCostura = "costura.ffxiv_act_plugin.memory.dll.compressed";
    private const string MemoryPrefix = "FFXIV_ACT_Plugin.Memory.Signatures_";
    public static readonly string[] MemoryVariants = ["Global", "TraditionalChinese"];
    private static readonly (string Variant, string Type)[] OodleClasses =
        [("Global", "Machina.FFXIV.Memory.SigScan"), ("Korean", "Machina.FFXIV.Memory.KoreanSigScan")];
    private const string OodleEnum = "Machina.FFXIV.Memory.SignatureType";

    // 클라이언트 지역이 쓰는 변형 (플러그인은 TC 만 따로, Machina 는 한국만 따로)
    public static string MemoryVariantFor(Region? r) => r?.Name == Region.TraditionalChinese.Name ? "TraditionalChinese" : "Global";
    public static string OodleVariantFor(Region? r) => r?.Name == Region.Korean.Name ? "Korean" : "Global";

    public static List<GameSig> Read(ModuleDefinition plugin)
    {
        var list = new List<GameSig>();
        using (var memory = Costura.ReadInner(plugin, MemoryCostura))
            foreach (string variant in MemoryVariants)
            {
                var res = Costura.Find(memory, MemoryPrefix + variant + ".txt");
                if (res == null) continue;
                foreach (var (name, value) in ParseMemory(Encoding.UTF8.GetString(res.GetResourceData())))
                    list.Add(new GameSig(SignatureKinds.Memory, variant, name, Spaced(value)));
            }
        using (var machina = Costura.ReadInner(plugin, Costura.Machina))
            foreach (var (variant, type) in OodleClasses)
                foreach (var (name, load) in OodleLoads(machina, type))
                    list.Add(new GameSig(SignatureKinds.Oodle, variant, name, FromOodleFormat((string)load.Operand)));
        return list;
    }

    // ---- memory -------------------------------------------------------------------------------

    public static string MemoryResource(string variant) => MemoryPrefix + variant + ".txt";

    private static IEnumerable<(string Name, string Value)> ParseMemory(string text) =>
        text.Split('\n')
            .Select(l => l.Trim().TrimStart('﻿').Split('|'))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .Select(p => (p[0].Trim(), p[1].Trim()));

    // 원문을 틀로 값만 바꾼다 (BOM/줄끝/순서 유지). 없는 이름은 Unknown 으로.
    public static (string Text, List<string> Unknown) ApplyMemory(string baseText, Dictionary<string, string> patterns)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Regex.Split(baseText, "(?<=\n)"))
        {
            string content = line.TrimEnd('\r', '\n');
            int bar = content.IndexOf('|');
            string name = bar > 0 ? content[..bar].TrimStart('﻿') : "";
            if (bar <= 0 || !patterns.TryGetValue(name, out string? pattern)) { sb.Append(line); continue; }
            seen.Add(name);
            sb.Append(content[..(bar + 1)]).Append(ToMemoryFormat(pattern)).Append(line[content.Length..]);
        }
        return (sb.ToString(), patterns.Keys.Where(k => !seen.Contains(k)).ToList());
    }

    // ---- oodle --------------------------------------------------------------------------------

    // Signatures getter 의 IL: ... ldc.i4 <SignatureType> ; ldstr "<패턴>" ; call SignatureStringToByteArray ...
    public static List<(string Name, Instruction Load)> OodleLoads(ModuleDefinition machina, string? variantOrType = null)
    {
        var enumType = machina.GetType(OodleEnum) ?? throw new ToolException($"Machina 에 {OodleEnum} 이(가) 없습니다.");
        var names = enumType.Fields.Where(f => f.HasConstant).ToDictionary(f => Convert.ToInt32(f.Constant), f => f.Name);
        string type = OodleClasses.FirstOrDefault(c => c.Variant == variantOrType).Type ?? variantOrType
                      ?? throw new ArgumentNullException(nameof(variantOrType));
        var getter = machina.GetType(type)?.Properties.FirstOrDefault(p => p.Name == "Signatures")?.GetMethod
                     ?? throw new ToolException($"Machina 에 {type}.Signatures 가 없습니다.");
        var result = new List<(string, Instruction)>();
        foreach (var ins in getter.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
            if (LdcValue(ins.Previous) is int v && names.TryGetValue(v, out string? name))
                result.Add((name, ins));
        return result;
    }

    public static IEnumerable<string> OodleVariants => OodleClasses.Select(c => c.Variant);

    private static int? LdcValue(Instruction? i) => i?.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1, Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)i.Operand,
        Code.Ldc_I4 => (int)i.Operand,
        _ => null,
    };

    // ---- 패치 (repack) -------------------------------------------------------------------------

    // FFXIV_ACT_Plugin.Memory.dll 의 Signatures_<변형>.txt 값을 바꾼다
    public static void PatchMemory(ModuleDefinition memory, List<Deucalion.Sig> inputs)
    {
        var variants = MemoryVariants.Where(v => Costura.Find(memory, MemoryResource(v)) != null).ToList();
        foreach (string variant in variants)
        {
            var values = SignatureInput.Resolve(inputs, variants).Where(x => x.Variant == variant)
                .ToDictionary(x => x.Name, x => x.Pattern, StringComparer.Ordinal);
            if (values.Count == 0) continue;
            var res = Costura.Find(memory, MemoryResource(variant))!;
            var (text, unknown) = ApplyMemory(Encoding.UTF8.GetString(res.GetResourceData()), values);
            if (unknown.Count > 0)
                throw new ToolException($"memory.txt: {variant} 에 없는 시그니처 이름입니다: {string.Join(", ", unknown)}");
            Costura.ReplaceInPlace(memory, res, new EmbeddedResource(res.Name, res.Attributes, Encoding.UTF8.GetBytes(text)));
            foreach (var (name, pattern) in values) Log.Info($"    ~ memory {variant}.{name}: {ToMemoryFormat(pattern)}");
        }
    }

    // Machina 의 SigScan / KoreanSigScan 문자열을 바꾼다
    public static void PatchOodle(ModuleDefinition machina, List<Deucalion.Sig> inputs)
    {
        var variants = OodleVariants.ToList();
        var resolved = SignatureInput.Resolve(inputs, variants);
        foreach (string variant in variants)
        {
            var mine = resolved.Where(x => x.Variant == variant).ToList();
            if (mine.Count == 0) continue; // 고칠 게 없는 변형은 (그 클래스가 없어도) 건드리지 않는다
            var loads = OodleLoads(machina, variant).ToDictionary(x => x.Name, x => x.Load, StringComparer.Ordinal);
            foreach (var (v, name, pattern) in mine)
            {
                if (!loads.TryGetValue(name, out var load))
                    throw new ToolException($"oodle.txt: 알 수 없는 시그니처입니다: '{name}' (쓸 수 있는 것: {string.Join(", ", loads.Keys)})");
                load.Operand = ToOodleFormat(pattern);
                Log.Info($"    ~ oodle {v}.{name}: {load.Operand}");
            }
        }
    }

    // ---- 표기 변환 ----------------------------------------------------------------------------

    // 바이트와 1바이트 와일드카드만 있는 패턴의 토큰 (null = 와일드카드)
    private static List<string?> Bytes(string pattern)
    {
        var toks = new List<string?>();
        string s = pattern.Replace(" ", "");
        for (int i = 0; i < s.Length; )
        {
            if (s[i] == '?') { toks.Add(null); i++; continue; }
            if (i + 1 < s.Length && s[i] == '*' && s[i + 1] == '*') { toks.Add(null); i += 2; continue; }
            if (i + 1 < s.Length && Uri.IsHexDigit(s[i]) && Uri.IsHexDigit(s[i + 1])) { toks.Add(s.Substring(i, 2).ToLowerInvariant()); i += 2; continue; }
            throw new ToolException($"시그니처 '{pattern}': 여기에는 바이트와 와일드카드만 쓸 수 있습니다.");
        }
        return toks;
    }

    private static string Spaced(string memoryValue) => string.Join(" ", Bytes(memoryValue).Select(t => t ?? "?"));

    // Machina 표기는 공백으로 나뉜 토큰이고 "**" / "??" 둘 다 1바이트 와일드카드다 (pelite 의 "??" 는 2바이트)
    private static string FromOodleFormat(string value) =>
        string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t is "**" or "??" or "?" ? "?" : t.ToLowerInvariant()));
    public static string ToMemoryFormat(string pattern) => string.Concat(Bytes(pattern).Select(t => t ?? "**"));
    public static string ToOodleFormat(string pattern) => string.Join(" ", Bytes(pattern).Select(t => t ?? "**"));
}
