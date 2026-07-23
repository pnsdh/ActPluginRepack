using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

// 데이터마이닝 CSV -> 플러그인이 내장하는 "id|Name" 리스트 변환.
// 규칙은 로더(DefinitionRepository)에서 역공학한 것으로, 공식 빌드 산출물과
// 바이트 단위로 대조 검증됨. 출력은 UTF-8(no BOM), "\r\n" 줄끝.
//
// 언어별 리스트 (<List>_<Lang>.txt):
//   ActionList        <- Action.csv   : "#"(16진수) -> Name, 빈 이름 제외
//   StatusList        <- Status.csv   : "#"(16진수) -> Name, 빈 이름 제외
//   TerritoryList     <- TerritoryType.csv : "#"(10진수) -> PlaceName[PlaceName].Name, 빈 이름 제외
//   ContentFinderList <- ContentFinderCondition.csv : 키=TerritoryType(10진수, "#"이 아님!),
//                        값=Name, 빈 이름/TT==0 제외, TT 중복 시 첫 행 우선
//
// 고정 3종 (플러그인이 지역 무관 _English 리소스만 읽어 고정 이름으로 생성):
//   ActionCategoryList_English <- Action.csv : "#"(16진수) -> ActionCategory(원문), id 0 제외
//   WorldList_English          <- World.csv  : "#"(10진수) -> Name, id 0/빈 이름 제외
//   MapList_English            <- Map.csv    : "#"(16진수) -> "지역|이름|서브"(PlaceName 조인), id 0 제외
//
// 생성하지 않는 것: ItemActionList (수작업 목록), ItemList/MountList (resource.dll 미참조).
internal static class CsvCommand
{
    // 다국어 리스트만 기본. 고정 3종(ActionCategory/World/Map)은 _English 를 덮어쓰므로 opt-in.
    public static readonly string[] DefaultLists = ["Action", "Status", "Territory", "ContentFinder"];
    public static readonly string[] PinnedLists = ["ActionCategory", "World", "Map"];

    private static readonly Dictionary<string, string> LangByDir = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ko"] = "Korean", ["en"] = "English", ["ja"] = "Japanese", ["de"] = "German",
        ["fr"] = "French", ["cn"] = "Chinese", ["tc"] = "TraditionalChinese",
    };

    public static int Run(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--csv-dir", "--out", "--lang", "--lists"]);
        string root = ProjectRoot.Resolve(o);
        string[] lists = o.GetList("--lists") is { Length: > 0 } l ? l : DefaultLists;
        Convert(
            csvDir: o.Get("--csv-dir", Path.Combine(root, "input", "csv", "ko")),
            outDir: o.Get("--out", Path.Combine(root, "data")),
            lang: o.Get("--lang"),
            lists);
        return 0;
    }

    public static void Convert(string csvDir, string outDir, string? lang, string[] lists)
    {
        csvDir = Path.GetFullPath(csvDir);
        if (!Directory.Exists(csvDir)) throw new ToolException($"csv dir not found: {csvDir}");
        if (lang == null)
        {
            string leaf = Path.GetFileName(csvDir.TrimEnd(Path.DirectorySeparatorChar));
            lang = LangByDir.GetValueOrDefault(leaf)
                   ?? throw new ToolException($"cannot infer --lang from '{leaf}'; pass --lang explicitly (e.g. Korean)");
        }
        Directory.CreateDirectory(outDir);

        Dictionary<string, string>? placeNames = null; // Territory/Map 이 공유 (PlaceName.csv 1회 로드)
        Dictionary<string, string> PlaceNames() => placeNames ??= LoadPlaceNames(csvDir);

        foreach (string list in lists)
        {
            switch (list)
            {
                case "Action":
                    WriteList(outDir, $"ActionList_{lang}.txt",
                        BuildIdValue(ReadCsv(csvDir, "Action.csv"), "Name", hex: true, skipZero: false));
                    break;
                case "Status":
                    WriteList(outDir, $"StatusList_{lang}.txt",
                        BuildIdValue(ReadCsv(csvDir, "Status.csv"), "Name", hex: true, skipZero: false));
                    break;

                // 고정, 언어 중립: id(16진수) -> ActionCategory 원문 그대로, id 0 제외
                case "ActionCategory":
                    WriteList(outDir, "ActionCategoryList_English.txt",
                        BuildIdValue(ReadCsv(csvDir, "Action.csv"), "ActionCategory", hex: true, skipZero: true));
                    break;

                // 고정: id(10진수) -> Name, id 0/빈 이름 제외
                case "World":
                    WriteList(outDir, "WorldList_English.txt",
                        BuildIdValue(ReadCsv(csvDir, "World.csv"), "Name", hex: false, skipZero: true));
                    break;

                case "ContentFinder":
                    WriteList(outDir, $"ContentFinderList_{lang}.txt", BuildContentFinder(csvDir));
                    break;
                case "Territory":
                    WriteList(outDir, $"TerritoryList_{lang}.txt", BuildTerritory(csvDir, PlaceNames()));
                    break;
                case "Map":
                    WriteList(outDir, "MapList_English.txt", BuildMap(csvDir, PlaceNames()));
                    break;

                default:
                    throw new ToolException($"unknown list '{list}'");
            }
        }
        Console.WriteLine($"[done] language={lang}");
    }

    // "#" -> 지정 컬럼. 빈 값(선택적으로 id 0도) 제외. hex 면 id 를 소문자 16진수로.
    private static List<string> BuildIdValue(Csv csv, string valCol, bool hex, bool skipZero)
    {
        int iId = csv.Col("#"), iVal = csv.Col(valCol);
        var lines = new List<string>();
        foreach (var r in csv.Rows)
        {
            if (r.Length <= iVal) continue;
            if (skipZero && r[iId] == "0") continue;
            string v = r[iVal];
            if (string.IsNullOrEmpty(v)) continue;
            uint id = ParseId(r[iId]);
            lines.Add($"{(hex ? id.ToString("x") : id.ToString(CultureInfo.InvariantCulture))}|{v}");
        }
        return lines;
    }

    // 키는 TerritoryType 이다("#"이 아님!). TT 중복 시 첫 행 우선.
    private static List<string> BuildContentFinder(string csvDir)
    {
        var cfc = ReadCsv(csvDir, "ContentFinderCondition.csv");
        int iName = cfc.Col("Name"), iTT = cfc.Col("TerritoryType");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var r in cfc.Rows)
        {
            if (r.Length <= Math.Max(iName, iTT)) continue;
            string nm = r[iName], tt = r[iTT];
            if (string.IsNullOrEmpty(nm) || tt == "0" || string.IsNullOrEmpty(tt)) continue;
            if (seen.Add(tt)) lines.Add($"{ParseId(tt)}|{nm}");
        }
        return lines;
    }

    private static List<string> BuildTerritory(string csvDir, Dictionary<string, string> placeNames)
    {
        var terr = ReadCsv(csvDir, "TerritoryType.csv");
        int iId = terr.Col("#"), iPN = terr.Col("PlaceName");
        var lines = new List<string>();
        foreach (var r in terr.Rows)
        {
            if (r.Length <= iPN) continue;
            string? nm = placeNames.GetValueOrDefault(r[iPN]);
            if (string.IsNullOrEmpty(nm)) continue;
            lines.Add($"{ParseId(r[iId])}|{nm}");
        }
        return lines;
    }

    // id(16진수) -> "지역|이름|서브", 각각 PlaceName 조인(없으면 빈 문자열). id 0 제외.
    private static List<string> BuildMap(string csvDir, Dictionary<string, string> placeNames)
    {
        var m = ReadCsv(csvDir, "Map.csv");
        int iId = m.Col("#"), iR = m.Col("PlaceNameRegion"), iP = m.Col("PlaceName"), iS = m.Col("PlaceNameSub");
        int need = Math.Max(Math.Max(iId, iR), Math.Max(iP, iS));
        var lines = new List<string>();
        foreach (var r in m.Rows)
        {
            if (r.Length <= need) continue;
            if (r[iId] == "0") continue;
            string P(int i) => placeNames.GetValueOrDefault(r[i], "");
            lines.Add($"{ParseId(r[iId]):x}|{P(iR)}|{P(iP)}|{P(iS)}");
        }
        return lines;
    }

    private static Dictionary<string, string> LoadPlaceNames(string csvDir)
    {
        var pnc = ReadCsv(csvDir, "PlaceName.csv");
        int iId = pnc.Col("#"), iName = pnc.Col("Name");
        var pn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in pnc.Rows)
            if (r.Length > iName) pn[r[iId]] = r[iName];
        return pn;
    }

    private static uint ParseId(string s)
    {
        if (!uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
            throw new ToolException($"invalid numeric id '{s}' (malformed csv?)");
        return id;
    }

    private static void WriteList(string outDir, string name, List<string> lines)
    {
        string outPath = Path.Combine(outDir, name);
        File.WriteAllText(outPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        Console.WriteLine($"  [+] {name} : {lines.Count} entries -> {outPath}");
    }

    private sealed record Csv(string Path, Dictionary<string, int> Index, List<string[]> Rows)
    {
        public int Col(string name) => Index.TryGetValue(name, out int i)
            ? i : throw new ToolException($"column '{name}' not found in {Path}");
    }

    // TextFieldParser: 따옴표 필드 처리, TrimWhiteSpace OFF(공식 빌드와 동일하게 공백 보존).
    private static Csv ReadCsv(string csvDir, string fileName)
    {
        string path = Path.Combine(csvDir, fileName);
        if (!File.Exists(path)) throw new ToolException($"csv not found: {path}");
        using var p = new TextFieldParser(path, Encoding.UTF8);
        p.SetDelimiters(",");
        p.HasFieldsEnclosedInQuotes = true;
        p.TrimWhiteSpace = false;
        string[] header = p.ReadFields() ?? throw new ToolException($"empty csv: {path}");
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < header.Length; i++) idx.TryAdd(header[i], i);
        var rows = new List<string[]>();
        while (!p.EndOfData) rows.Add(p.ReadFields()!);
        return new Csv(path, idx, rows);
    }
}
