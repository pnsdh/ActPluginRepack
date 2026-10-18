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
internal static class CsvConverter
{
    // csv 커맨드의 기본은 다국어 리스트만. 고정 3종(ActionCategory/World/Map)은 _English 를 덮어쓰므로 --lists 로 명시
    // (build 는 input\csv\en 이 있을 때 자동으로 넣는다).
    public static readonly string[] DefaultLists = ["Action", "Status", "Territory", "ContentFinder"];
    public static readonly string[] PinnedLists = ["ActionCategory", "World", "Map"];

    // 위 리스트들이 읽는 시트 전부 (prepare 가 SharpVision 추출본에서 이것만 가져온다).
    // Convert 에 ReadCsv 대상을 추가하면 여기도 추가할 것.
    public static readonly string[] RequiredSheets =
        ["Action", "Status", "TerritoryType", "PlaceName", "ContentFinderCondition", "World", "Map"];

    // cn/tc 폴더: 플러그인에 cn/tc 리소스가 Action/Status 리스트뿐이라 이것만 변환한다.
    public static readonly string[] ChineseLists = ["Action", "Status"];
    public static readonly string[] ChineseSheets = ["Action", "Status"];
    public static bool IsChineseLeaf(string leaf) =>
        leaf.Equals("cn", StringComparison.OrdinalIgnoreCase) || leaf.Equals("tc", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> LangByDir = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ko"] = "Korean", ["en"] = "English", ["ja"] = "Japanese", ["de"] = "German",
        ["fr"] = "French", ["cn"] = "Chinese", ["tc"] = "TraditionalChinese",
    };

    public static void Convert(string csvDir, string outDir, string? lang, string[] lists)
    {
        csvDir = Path.GetFullPath(csvDir);
        if (!Directory.Exists(csvDir)) throw new ToolException($"CSV 폴더가 없습니다: {csvDir}");
        if (lang == null)
        {
            string leaf = Path.GetFileName(csvDir.TrimEnd(Path.DirectorySeparatorChar));
            lang = LangByDir.GetValueOrDefault(leaf)
                   ?? throw new ToolException($"폴더 이름 '{leaf}' 에서 언어를 알 수 없습니다. --lang 으로 직접 지정하세요 (예: Korean).");
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
                    throw new ToolException($"알 수 없는 리스트입니다: '{list}'");
            }
        }
        Log.Info($"[csv] 완료 언어={lang}");
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
            throw new ToolException($"숫자 ID 가 올바르지 않습니다: '{s}' (CSV 가 깨졌을 수 있음)");
        return id;
    }

    // 새로 만든 "id|이름" 목록에 없는 id 를 baseText(공식 플러그인의 같은 목록)에서 가져와 남긴다 (10진수 id 순).
    // 서버 목록은 글로벌 클라이언트의 World 로 만드는데, 공식 플러그인에는 다른 지역(대만 등) 서버도 들어 있다.
    public static void KeepMissingIds(string path, string? baseText)
    {
        if (baseText == null || !File.Exists(path)) return;
        static string Id(string line) => line[..Math.Max(0, line.IndexOf('|'))];
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0).ToList();
        var have = lines.Select(Id).ToHashSet(StringComparer.Ordinal);
        var extra = baseText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.IndexOf('|') > 0 && !have.Contains(Id(l))).Distinct().ToList();
        if (extra.Count == 0) return;
        Log.Note($"[csv] {Path.GetFileName(path)}: 공식 플러그인에만 있는 {extra.Count}개(다른 지역 서버 등)는 남김");
        var all = lines.Concat(extra)
            .OrderBy(l => uint.TryParse(Id(l), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : uint.MaxValue).ToList();
        File.WriteAllText(path, string.Join("\r\n", all) + "\r\n", new UTF8Encoding(false));
    }

    private static void WriteList(string outDir, string name, List<string> lines)
    {
        // 0건 리스트를 넣으면 그 언어의 이름이 통째로 사라진다 (잘린 CSV, 다른 언어 클라이언트 등)
        if (lines.Count == 0) throw new ToolException($"{name}: 항목이 0건입니다. CSV 가 비었거나 잘못되었을 수 있어 빈 리스트로 덮어쓰지 않았습니다.");
        // UTF-8 이 아닌 CSV(엑셀에서 다시 저장 등)는 글자가 깨진 채로 읽힌다
        if (lines.Any(l => l.Contains('�')))
            throw new ToolException($"{name}: 깨진 글자가 있습니다. CSV 가 UTF-8 이 아닙니다. 엑셀 등에서 다시 저장했다면 원본을 다시 받으세요.");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, name);
        File.WriteAllText(outPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        Log.Info($"[csv] {name} : {lines.Count}개 -> {outPath}");
    }

    private sealed record Csv(string Path, Dictionary<string, int> Index, List<string[]> Rows)
    {
        public int Col(string name) => Index.TryGetValue(name, out int i)
            ? i : throw new ToolException($"{Path} 에 '{name}' 열이 없습니다.");
    }

    // TextFieldParser: 따옴표 필드 처리, TrimWhiteSpace OFF(공식 빌드와 동일하게 공백 보존).
    // 형식 두 가지:
    //   xiv-data-oxidizer / SharpVision : 1행 "#,Name,..." 헤더, 2행부터 데이터
    //   SaintCoinach (cn/tc datamining) : 1행 "key,0,1,..." 열 번호, 2행 "#,Name,..." 헤더, 3행 타입, 4행부터 데이터
    private static Csv ReadCsv(string csvDir, string fileName)
    {
        string path = Path.Combine(csvDir, fileName);
        if (!File.Exists(path)) throw new ToolException($"CSV 파일이 없습니다: {path}");
        using var p = new TextFieldParser(path, Encoding.UTF8);
        p.SetDelimiters(",");
        p.HasFieldsEnclosedInQuotes = true;
        p.TrimWhiteSpace = false;
        string[] header = p.ReadFields() ?? throw new ToolException($"CSV 파일이 비어 있습니다: {path}");
        if (header.Length > 0 && header[0].TrimStart('﻿') == "key")
        {
            header = p.ReadFields() ?? throw new ToolException($"SaintCoinach CSV 파일이 잘렸습니다: {path}");
            p.ReadFields(); // 타입 행
        }
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < header.Length; i++) idx.TryAdd(header[i], i);
        var rows = new List<string[]>();
        try
        {
            while (!p.EndOfData) rows.Add(p.ReadFields()!);
        }
        catch (MalformedLineException ex)
        {
            throw new ToolException($"CSV 형식이 잘못되었습니다 ({path}, {ex.LineNumber}행). 파일이 잘렸거나 편집 중에 깨졌을 수 있습니다.");
        }
        return new Csv(path, idx, rows);
    }
}
