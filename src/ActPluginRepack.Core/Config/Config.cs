using System.Text.Json;
using System.Text.Json.Nodes;

// 루트의 config.json (사용자별 설정, git 미추적). 형식은 config.example.json 참고.
//
//   {
//     "sharpVision": "D:\\...\\SharpVision.exe",
//     "oxidizer": true,                                        (선택: SharpVision 대신 xiv-data-oxidizer, git + Rust 필요;
//                                                               직접 관리하는 clone 이면 그 폴더 경로)
//     "ida": "C:\\Program Files\\IDA Professional 9.4",       (선택: opcode 직접 추출. 없으면 Program Files 에서 찾고,
//                                                               false 면 쓰지 않음)
//     "idaTarget": "korean",                                   (선택: IDA 로 분석할 exe 의 지역. 기본 simplified-chinese —
//                                                               설정에 없는 지역이면 같은 버전 exe 를 공식 패치 서버에서 받음)
//     "localDiff": false,                                      (선택: opcodediff 로컬 실행을 끔. 기본은 켜짐 —
//                                                               Python 3.12+ 가 없으면 휴대용 Python 을 받아 씀)
//     "python": "C:\\...\\python.exe",                         (선택: localDiff 용 Python 지정)
//     "clients": { "korean": "C:\\...\\FINAL FANTASY XIV - KOREA", "global": "D:\\..." }
//   }
//
// clients 의 키는 지역(Region.All). 경로는 설치 폴더 / game 폴더 / ffxiv_dx11.exe 어느 것이든 된다.
internal sealed record GameClient(string Name, Region? Region, string ExePath, string? Version)
{
    public string GameDir => Path.GetDirectoryName(ExePath)!;
    public string SqpackDir => Path.Combine(GameDir, "sqpack");
}

// ClientErrors: 경로가 틀린 클라이언트 (설정 전체를 못 쓰게 만들지 않고 따로 알린다 — 나머지 클라이언트는 쓴다)
internal sealed record AppConfig(List<GameClient> Clients, string? SharpVision, string? Ida, bool LocalDiff = true,
                                 string? Python = null, string? Oxidizer = null)
{
    public List<string> ClientErrors { get; init; } = [];
    public bool IdaDetected { get; init; } // Ida 를 설정이 아니라 자동으로 찾음
    // IDA 로 분석할 exe 의 지역 (통합 클라이언트 지역끼리는 opcode 가 같다). 기본은 중국판 — finder 의 시그니처가
    // 중국판 기준이라 보정 없이 맞는다. 그 지역 클라이언트가 설정에 없으면 같은 버전 exe 를 공식 패치 서버에서 받는다.
    public string IdaTarget { get; init; } = DefaultIdaTarget;
    public const string DefaultIdaTarget = "simplified-chinese";
}

// 클라이언트 지역. 이름은 SharpVision 의 추출 폴더명(output/extract/<name>)과 같다.
//   Label             화면에 보이는 한국어 이름
//   SharpVisionLangs  SharpVision --lang 코드
//   CsvLeafs          input\csv\<leaf> 폴더명 (SharpVisionLangs 와 같은 순서)
//   Opcodes           input\opcodes\<Opcodes>.txt / Machina 의 opcode 리소스 / OverlayPlugin opcodes.jsonc 지역명
//   CnOpcodes         CN 클라이언트와 opcode 가 같음 (7.5 통합 클라이언트) -> Yarukon CN 결과를 쓸 수 있다
//   Karashiiro        karashiiro/FFXIVOpcodes 의 region
//   Lemegeton         Lemegeton blueprint 의 Region Name (없으면 출력 안 함)
internal sealed record Region(string Name, string Label, string[] SharpVisionLangs, string[] CsvLeafs, string Opcodes, bool CnOpcodes,
                              string Karashiiro, string? Lemegeton)
{
    public static readonly Region[] All =
    [
        new("global", "글로벌", ["en", "ja", "de", "fr"], ["en", "ja", "de", "fr"], "Global", CnOpcodes: true, "Global", "EN/DE/FR/JP"),
        new("simplified-chinese", "중국", ["chs"], ["cn"], "Chinese", CnOpcodes: true, "CN", "CN"),
        new("korean", "한국", ["ko"], ["ko"], "Korean", CnOpcodes: true, "KR", "KR"),
        new("traditional-chinese", "대만", ["cht"], ["tc"], "TraditionalChinese", CnOpcodes: false, "TW", null),
    ];

    // 공개 추출본(xiv-data-oxidizer 출력, fae-industries/xiv-data)의 언어 폴더 이름 — input\csv\<leaf> 와 cn 만 다르다
    public static string DataLang(string leaf) => leaf == "cn" ? "chs" : leaf;

    public static Region? Find(string name) =>
        All.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // 지역을 모르는 클라이언트(직접 고른 폴더 등)도 화면에 보일 수 있게
    public static string LabelOf(Region? region) => region?.Label ?? "지역 모름";
}

internal static class Config
{
    public const string FileName = "config.json";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // 한 항목만 바꿔 저장한다 (GUI 설정 화면용). 다른 항목은 그대로 두지만 주석은 사라진다.
    // value 가 null 이면 그 항목을 지운다. 쓰기는 임시 파일을 거쳐 원자적으로.
    public static void Set(string root, string key, JsonNode? value)
    {
        string path = Path.Combine(root, FileName);
        JsonObject obj;
        try
        {
            obj = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject ?? [] : [];
        }
        catch (JsonException ex)
        {
            throw new ToolException($"{FileName} 파일을 읽지 못했습니다: {ex.Message}");
        }
        if (value == null) obj.Remove(key);
        else obj[key] = value;
        Files.WriteAtomic(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // 읽을 수 없는(JSON 이 깨진) config.json 을 옆에 보관하고 치운다 — 화면에서 새로 설정할 수 있게. 보관한 경로를 돌려준다.
    public static string? MoveBroken(string root)
    {
        string path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return null;
        try { using var _ = JsonDocument.Parse(File.ReadAllText(path), ReadOptions); return null; } // 문법은 멀쩡함
        catch (JsonException) { }
        string kept = path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(path, kept);
        Log.Note($"[config] 읽을 수 없는 {FileName} 을(를) {Path.GetFileName(kept)} 로 보관하고 새로 만듦");
        return kept;
    }

    // 게임 클라이언트 목록을 저장 (지역 이름 -> 설치 폴더)
    public static void SetClients(string root, IReadOnlyDictionary<string, string> clients)
    {
        if (clients.Keys.FirstOrDefault(k => Region.Find(k) == null) is { } bad)
            throw new ToolException($"알 수 없는 지역입니다: '{bad}'");
        var node = new JsonObject();
        foreach (var (region, dir) in clients) node[Region.Find(region)!.Name] = dir;
        Set(root, "clients", node);
    }

    public static AppConfig Load(string root)
    {
        string path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return new AppConfig([], null, Ida.Detect()) { IdaDetected = true };

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ToolException($"{FileName} 파일을 읽지 못했습니다: {ex.Message}\n경로에 쓴 \\ 는 \\\\ 로 쓰거나 / 로 바꿔 쓰세요.");
        }

        using (doc)
        {
            var clients = new List<GameClient>();
            var clientErrors = new List<string>();
            if (doc.RootElement.TryGetProperty("clients", out var cs))
            {
                if (cs.ValueKind != JsonValueKind.Object)
                    throw new ToolException($"{FileName} 의 'clients' 는 '지역: 경로' 형식의 객체여야 합니다.");
                foreach (var p in cs.EnumerateObject())
                {
                    var region = Region.Find(p.Name) ?? throw new ToolException(
                        $"{FileName} 의 clients 에 알 수 없는 지역이 있습니다: '{p.Name}'. {string.Join(" / ", Region.All.Select(r => r.Name))} 중 하나를 쓰세요.");
                    // 대소문자만 다른 같은 지역 키 ("Korean" 과 "korean") — 지역마다 클라이언트는 하나
                    if (clients.Any(c => c.Region == region) || clientErrors.Any(e => e.StartsWith(region.Name + ":", StringComparison.Ordinal)))
                    {
                        clientErrors.Add($"{region.Name}: {FileName} 의 clients 에 같은 지역이 두 번 있습니다. 하나만 남기세요.");
                        continue;
                    }
                    try { clients.Add(ResolveClient(region.Name, region, Path.GetFullPath(PathValue(p.Value, $"clients.{p.Name}"), root))); }
                    catch (ToolException ex) { clientErrors.Add(ex.Message); }
                }
            }

            // 상대 경로는 실행한 위치가 아니라 프로젝트 폴더 기준
            string? OptionalPath(string key) =>
                doc.RootElement.TryGetProperty(key, out var v) ? Path.GetFullPath(PathValue(v, key), root) : null;
            // localDiff: 기본 켜짐 (도구와, 설치된 Python 3.12+ 가 없으면 휴대용 Python 까지 자동으로 받는다)
            bool localDiff = !doc.RootElement.TryGetProperty("localDiff", out var ld) || ld.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ToolException($"{FileName} 의 localDiff 는 true 나 false 여야 합니다."),
            };
            // ida: 문자열 = 설치 폴더, false = 쓰지 않음, 없으면 Program Files 에서 찾는다
            bool idaDetected = !doc.RootElement.TryGetProperty("ida", out var idaElement);
            string? ida = idaDetected ? Ida.Detect()
                : idaElement.ValueKind == JsonValueKind.False ? null
                : Path.GetFullPath(PathValue(idaElement, "ida"), root);
            // oxidizer: true = tools\ 에 받아 빌드, 문자열 = 직접 관리하는 clone 경로
            string? oxidizer = doc.RootElement.TryGetProperty("oxidizer", out var ox) ? ox.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => null,
                _ => Path.GetFullPath(PathValue(ox, "oxidizer"), root),
            } : null;
            // idaTarget: IDA 로 분석할 exe 의 지역 (기본 simplified-chinese)
            string idaTarget = doc.RootElement.TryGetProperty("idaTarget", out var it)
                ? ParseIdaTarget(it.ValueKind == JsonValueKind.String ? it.GetString() : null)
                : AppConfig.DefaultIdaTarget;
            return new AppConfig(clients, OptionalPath("sharpVision"), ida, localDiff, OptionalPath("python"),
                                 oxidizer) { ClientErrors = clientErrors, IdaDetected = idaDetected, IdaTarget = idaTarget };
        }
    }

    // IDA 분석 대상 지역: opcode 가 같은 통합 클라이언트 지역만 (대만판은 그 클라이언트 자신으로만 분석)
    public static string ParseIdaTarget(string? value) =>
        Region.Find(value ?? "") is { CnOpcodes: true } r ? r.Name
            : throw new ToolException($"IDA 분석 대상(idaTarget / --ida-target)으로 쓸 수 없는 값입니다: '{value}'. " +
                                      string.Join(" / ", Region.All.Where(x => x.CnOpcodes).Select(x => x.Name)) + " 중 하나를 쓰세요.");

    // --game a,b 로 받은 경로들. 지역은 모르므로 이름만 game1, game2...
    public static List<GameClient> FromPaths(IEnumerable<string> paths) =>
        paths.Select((p, i) => ResolveClient($"game{i + 1}", null, p)).ToList();

    private static string PathValue(JsonElement e, string what) =>
        e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())
            ? e.GetString()!
            : throw new ToolException($"{FileName} 의 {what} 값은 경로 문자열이어야 합니다.");

    private static GameClient ResolveClient(string name, Region? region, string path)
    {
        string full = Path.GetFullPath(path);
        string? exe = new[]
        {
            full,
            Path.Combine(full, "ffxiv_dx11.exe"),
            Path.Combine(full, "game", "ffxiv_dx11.exe"),
        }.FirstOrDefault(File.Exists);
        if (exe == null) throw new ToolException($"{name}: {full} 에 ffxiv_dx11.exe 가 없습니다.");

        // 버전 문자열은 폴더 이름·비교에 쓰이므로 형식이 맞을 때만 (아니면 null -> prepare 가 알려 준다)
        return new GameClient(name, region, exe, GameVersion.ReadFrom(Path.GetDirectoryName(exe)!));
    }
}
