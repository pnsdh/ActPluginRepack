using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

// opcode 한 벌: 서버 패킷 이름 -> opcode. 이름은 표준 이름(OpcodeNames)으로 맞춰 둔다.
internal sealed record OpcodeSet(string Source, Dictionary<string, int> Server, string? PatchLabel = null);

// 표준 이름 = Yarukon ffxiv_opcode_finder 의 ServerZoneIpcType 이름. 출력 형식마다 이름이 달라 여기서 잇는다.
internal static class OpcodeNames
{
    // Machina (input\opcodes\<지역>.txt) — Yarukon machina.template 과 같은 대응
    public static readonly (string Out, string Name)[] Machina =
    [
        ("StatusEffectList", "StatusEffectList"), ("StatusEffectList2", "StatusEffectList2"),
        ("StatusEffectList3", "StatusEffectList3"), ("BossStatusEffectList", "BossStatusEffectList"),
        ("StatusEffectListForay3", "StatusEffectListForay3"),
        ("Ability1", "ActionEffect1"), ("Ability8", "ActionEffect8"), ("Ability16", "ActionEffect16"),
        ("Ability24", "ActionEffect24"), ("Ability32", "ActionEffect32"),
        ("ActorCast", "ActorCast"), ("EffectResult", "EffectResult"), ("EffectResultBasic", "EffectResultBasic"),
        ("ActorControl", "ActorControl"), ("ActorControlSelf", "ActorControlSelf"), ("ActorControlTarget", "ActorControlTarget"),
        ("UpdateHpMpTp", "UpdateHpMpTp"), ("PlayerSpawn", "PlayerSpawn"), ("NpcSpawn", "NpcSpawn"), ("NpcSpawn2", "NpcSpawn2"),
        ("ActorMove", "ActorMove"), ("ActorSetPos", "ActorSetPos"), ("ActorGauge", "ActorGauge"),
        ("PresetWaymark", "PlaceFieldMarkerPreset"), ("Waymark", "PlaceFieldMarker"), ("SystemLogMessage", "SystemLogMessage"),
    ];

    // Lemegeton blueprint — Yarukon finder 의 lemegeton.xml 과 같은 대응/순서
    public static readonly (string Out, string Name)[] Lemegeton =
    [
        ("StatusEffectList", "StatusEffectList"), ("StatusEffectList2", "StatusEffectList2"), ("StatusEffectList3", "StatusEffectList3"),
        ("Ability1", "ActionEffect1"), ("Ability8", "ActionEffect8"), ("Ability16", "ActionEffect16"),
        ("Ability24", "ActionEffect24"), ("Ability32", "ActionEffect32"),
        ("ActorCast", "ActorCast"), ("EffectResult", "EffectResult"),
        ("ActorControl", "ActorControl"), ("ActorControlSelf", "ActorControlSelf"), ("ActorControlTarget", "ActorControlTarget"),
        ("MapEffect", "MapEffect"), ("EventPlay", "EventPlay"), ("EventPlay64", "EventPlay64"),
    ];

    // OverlayPlugin resources\opcodes.jsonc
    public static readonly (string Out, string Name)[] OverlayPlugin =
    [
        ("MapEffect", "MapEffect"), ("MapEffect4", "MapEffect4"), ("MapEffect8", "MapEffect8"), ("MapEffect12", "MapEffect12"),
        ("CEDirector", "CEDirector"), ("RSVData", "RSV"), ("NpcYell", "NpcYell"), ("BattleTalk2", "BattleTalk2"),
        ("Countdown", "CountdownInitiate"), ("CountdownCancel", "CountdownCancel"),
        ("ActorMove", "ActorMove"), ("ActorSetPos", "ActorSetPos"),
    ];

    // karashiiro/FFXIVOpcodes 이름 -> 표준 이름 (다른 것만. 7.57 에서 값으로 대조해 만든 표)
    public static readonly Dictionary<string, string> FromKarashiiro = new(StringComparer.Ordinal)
    {
        ["StatusEffectListBozja"] = "StatusEffectList2", ["StatusEffectListPlayer"] = "StatusEffectList3",
        ["StatusEffectListDouble"] = "BossStatusEffectList", ["StatusEffectListOccult"] = "StatusEffectListForay3",
        ["Effect"] = "ActionEffect1", ["AoeEffect8"] = "ActionEffect8", ["AoeEffect16"] = "ActionEffect16",
        ["AoeEffect24"] = "ActionEffect24", ["AoeEffect32"] = "ActionEffect32", ["BossSpawn"] = "NpcSpawn2",
        ["EnvironmentControl"] = "MapEffect", ["EnvironmentControl4"] = "MapEffect4",
        ["EnvironmentControl8"] = "MapEffect8", ["EnvironmentControl12"] = "MapEffect12",
        ["RSVData"] = "RSV", ["Countdown"] = "CountdownInitiate",
    };

    // 어느 출력이든 쓰는 표준 이름 전부 (대조/보충 범위)
    public static readonly string[] Used =
        Machina.Concat(Lemegeton).Concat(OverlayPlugin).Select(m => m.Name).Distinct().ToArray();
}

// opcode 출처.
//   Yarukon     Yarukon/FFXIVNetworkOpcodes 의 output/CN_<날짜>/opcodes.json — IDA 로 CN 클라이언트를 분석한
//               결과. 7.5 이후 CN/Global/KR 클라이언트가 같은 opcode 를 쓰므로(7.57 공식 3.0.3.3 과 26/26 일치)
//               게임 버전 날짜만 맞으면 세 지역에 쓴다.
//   opcodediff  xivdev/opcodediff 의 diffs/<패치>.diff.json — 직전 버전 -> 이번 버전 opcode 대응표. 직전 버전
//               opcode 목록에 적용해 이번 버전을 얻는다 (바이너리 불필요).
//   karashiiro  karashiiro/FFXIVOpcodes 의 opcodes.min.json — Global/CN/KR/TW 별도, 버전은 패치 번호(7.57).
//               GameVersions 대응표로 게임 버전과 맞춘다 (대응표에 없으면 베이스 플러그인과의 차이로 추정).
internal static class OpcodeSources
{
    private const string YarukonRaw = "https://raw.githubusercontent.com/Yarukon/FFXIVNetworkOpcodes/master/output";
    private const string KarashiiroUrl = "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/master/opcodes.min.json";

    // 게임 버전 "2026.10.06.0000.0000" -> Yarukon 폴더 "CN_2026.10.06".
    public static string YarukonFolder(string gameVersion) => "CN_" + gameVersion[..Math.Min(10, gameVersion.Length)];

    // 공개 전이면 null.
    public static OpcodeSet? FetchYarukon(string gameVersion)
    {
        string folder = YarukonFolder(gameVersion);
        string? json = Http.GetStringOrNull($"{YarukonRaw}/{folder}/opcodes.json");
        return json == null ? null : new OpcodeSet($"Yarukon {folder}", ParseIpcJson(json, $"Yarukon {folder}", aliases: null));
    }

    // region(Global/CN/KR/TW) -> opcode. 받지 못하면 빈 사전.
    public static Dictionary<string, OpcodeSet> FetchKarashiiro()
    {
        string? json = Http.GetStringOrNull(KarashiiroUrl);
        var result = new Dictionary<string, OpcodeSet>(StringComparer.Ordinal);
        if (json == null) return result;
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            string region = entry.GetProperty("region").GetString()!;
            string version = entry.GetProperty("version").GetString() ?? "?";
            result[region] = new OpcodeSet($"karashiiro {region} {version}",
                ReadList(entry.GetProperty("lists"), OpcodeNames.FromKarashiiro), PatchLabel: version);
        }
        return result;
    }

    // xivdev/opcodediff 의 diffs/<패치>.diff.json: [{"old":["0x346"],"new":["0x89"]}, ...] — 직전 버전 opcode ->
    // 이번 버전 opcode. 1:1 항목만 쓴다. 공개 전이면 null.
    public static Dictionary<int, int>? FetchOpcodediff(string retail)
    {
        string url = $"https://raw.githubusercontent.com/xivdev/opcodediff/master/diffs/{retail}.diff.json";
        string? json = Http.GetStringOrNull(url);
        return json == null ? null : ParseDiff(json, $"opcodediff {retail}");
    }

    public static Dictionary<int, int> ParseDiff(string json, string what)
    {
        var map = new Dictionary<int, int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                // null 이나 빈 값을 0 으로 읽지 않게 (Convert.ToInt32(null, 16) 은 0)
                var olds = e.GetProperty("old").EnumerateArray().Select(v => v.GetString() is { Length: > 0 } x ? x : throw new FormatException("빈 값")).ToList();
                var news = e.GetProperty("new").EnumerateArray().Select(v => v.GetString() is { Length: > 0 } x ? x : throw new FormatException("빈 값")).ToList();
                if (olds.Count == 1 && news.Count == 1) map[Convert.ToInt32(olds[0], 16)] = Convert.ToInt32(news[0], 16);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ToolException($"{what} 의 diff.json 형식이 예상과 다릅니다: {ex.Message}");
        }
        return map;
    }

    // {"lists": {"ServerZoneIpcType": [{"name":..,"opcode":..}]}} (Yarukon/karashiiro 공통 형식)
    public static Dictionary<string, int> ParseIpcJson(string json, string what, Dictionary<string, string>? aliases)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ReadList(doc.RootElement.GetProperty("lists"), aliases);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ToolException($"{what} 의 opcodes.json 형식이 예상과 다릅니다: {ex.Message}");
        }
    }

    private static Dictionary<string, int> ReadList(JsonElement lists, Dictionary<string, string>? aliases)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in lists.GetProperty("ServerZoneIpcType").EnumerateArray())
        {
            string name = e.GetProperty("name").GetString()!;
            map[aliases?.GetValueOrDefault(name) ?? name] = e.GetProperty("opcode").GetInt32();
        }
        return map;
    }
}

// Machina 형식 (input\opcodes\<지역>.txt, "이름|16진수").
internal static class MachinaOpcodes
{
    // 베이스 플러그인의 Machina 에 들어 있는 <Region>.txt 원문.
    public static string ReadBase(ModuleDefinition plugin, string region)
    {
        using var machina = Costura.ReadInner(plugin, Costura.Machina);
        string name = Costura.OpcodePrefix + region + ".txt";
        var res = Costura.Find(machina, name) ?? throw new ToolException($"베이스 플러그인에 {name} 리소스가 없습니다.");
        return Encoding.UTF8.GetString(res.GetResourceData());
    }

    public static Dictionary<string, int> Parse(string text, string what)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            int bar = line.IndexOf('|');
            if (bar <= 0 || !int.TryParse(line[(bar + 1)..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                throw new ToolException($"{what}: opcode 줄의 형식이 잘못되었습니다: '{line}'");
            map[line[..bar].Trim()] = v;
        }
        return map;
    }

    // 표준 이름 opcode -> Machina 이름 opcode
    public static Dictionary<string, int> FromSet(OpcodeSet set) =>
        OpcodeNames.Machina.Where(m => set.Server.ContainsKey(m.Name))
            .ToDictionary(m => m.Out, m => set.Server[m.Name], StringComparer.Ordinal);

    public sealed record Merge(string Text, int Changed, List<string> Missing);

    // 베이스 원문을 틀로 값만 바꾼다 — BOM/줄끝/순서/이름 집합이 베이스 그대로 유지된다.
    // values 에 없는 이름은 베이스 값 유지(Missing).
    public static Merge Apply(string baseText, Dictionary<string, int> values)
    {
        var sb = new StringBuilder(baseText.Length);
        var missing = new List<string>();
        int changed = 0;
        foreach (string line in Regex.Split(baseText, "(?<=\n)")) // 줄끝을 붙인 채로
        {
            string content = line.TrimEnd('\r', '\n');
            string eol = line[content.Length..];
            int bar = content.IndexOf('|');
            if (bar <= 0) { sb.Append(line); continue; }
            string name = content[..bar].TrimStart('﻿');
            if (!values.TryGetValue(name, out int value)) { missing.Add(name); sb.Append(line); continue; }
            if (value != Convert.ToInt32(content[(bar + 1)..], 16)) changed++;
            sb.Append(content[..(bar + 1)]).Append(value.ToString("X")).Append(eol);
        }
        return new Merge(sb.ToString(), changed, missing);
    }
}
