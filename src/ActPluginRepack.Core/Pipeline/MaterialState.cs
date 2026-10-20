using System.Text.Json;

// input\.state.json — 마지막 prepare 가 재료를 어느 게임 버전 / 베이스 플러그인 버전 기준으로 만들었는지.
// build 는 이걸 지금 상태와 비교해 오래된 재료로 빌드하는 걸 알린다 (게임이 또 패치됐거나 플러그인이 바뀐 경우).
internal sealed record MaterialState(
    DateTime PreparedAt,
    string? PluginVersion,
    Dictionary<string, string> GameVersions,          // 지역 -> 게임 버전
    Dictionary<string, MaterialState.OpcodeInfo> Opcodes, // 지역 -> 출처 / 바뀐 수
    bool? HooksOkOfficially,  // null: 점검 안 함
    bool? PatchNeeded,        // null: 판단할 수 없음
    bool MemoryFixedByInput = false,
    [property: System.Text.Json.Serialization.JsonPropertyName("Skipped")] // 예전 기록과 같은 키
    Dictionary<string, string>? Outdated = null, // 최신 버전이 아니라 이번 재료에서 뺀 클라이언트: 지역 -> 그때의 게임 버전
    List<string>? Unconfirmed = null,            // 이번 버전 것으로 확인된 공개 자료 없이 정한 opcode 의 지역 (공개되면 다시 확인)
    // prepare 의 결론 (예전 기록에는 없음). 이름으로 저장한다 — 순서가 바뀌어도 뜻이 바뀌지 않게 (숫자로 된 기록도 읽힘)
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PrepareOutcome>))]
    PrepareOutcome? Outcome = null,
    bool FromServer = false, // 내 게임 대신 공식 패치 서버의 최신 exe 로 준비함 (GameVersions 는 그 버전, CSV 없음)
    // 교차 확인: null 이면 하지 않음, 비었으면 다른 것 없음, 아니면 출처·수단끼리 다른 항목 (hotfix 가 경고·릴리스 설명에)
    List<string>? CrossCheck = null,
    // 교차 확인에서 대조하지 못한 것 (분류째로 하지 않았거나 실패, 또는 대조할 짝이 없던 항목) — 있으면 hotfix 가 경고
    List<string>? CrossCheckGaps = null)
{
    public sealed record OpcodeInfo(string Source, int Changed, int Total, int Missing = 0);

    // 마지막 prepare 기록. 없거나 읽지 못하면 null (깨졌거나 잠깐 잠긴 기록도 없는 것으로 — 상태 화면 전체가 실패하지 않게)
    public static MaterialState? Load(Workspace ws)
    {
        if (!File.Exists(ws.StatePath)) return null;
        try { return JsonSerializer.Deserialize<MaterialState>(File.ReadAllText(ws.StatePath), JsonOptions.Indented); }
        catch (Exception ex) when (ex is JsonException || Errors.IsFileError(ex)) { return null; }
    }

    public void Save(Workspace ws) => Files.WriteAtomic(ws.StatePath, JsonSerializer.Serialize(this, JsonOptions.Indented));

    // 지금 상태와 다른 점 (빌드 전에 알릴 것). 비어 있으면 최신.
    // 공식 패치 서버 exe 로 준비했으면 내 게임이 그보다 옛 버전인 것은 차이가 아니다(업데이트 전에 미리 준비) — 기록에 없는 지역이거나
    // 내 게임이 준비한 버전보다 새 버전이 됐으면 알린다 (그 뒤의 새 패치는 인터넷 없이 알 수 없어 보지 않음)
    public List<string> Differences(string pluginPath, IEnumerable<GameClient> clients)
    {
        var diffs = new List<string>();
        string? plugin = PluginVersionOf(pluginPath);
        if (plugin != PluginVersion)
            diffs.Add($"마지막 prepare 이후 베이스 플러그인이 바뀌었습니다 ({PluginVersion ?? "?"} -> {plugin ?? "없음"}).");
        foreach (var c in clients.Where(c => c.Region != null && !IsOutdated(c)))
        {
            if (!GameVersions.TryGetValue(c.Region!.Name, out string? prepared))
                diffs.Add($"{c.Name}: 마지막 prepare 기록에 없는 클라이언트입니다.");
            else if (FromServer && Thaliak.Supports(c.Region) && c.Version != null && !Thaliak.IsOlder(prepared, c.Version))
                continue;
            else if (prepared != c.Version)
                diffs.Add($"{c.Name}: 마지막 prepare 이후 게임이 패치되었습니다 ({prepared} -> {c.Version}).");
        }
        return diffs;
    }

    // 이 재료를 확인한 클라이언트 (build 의 시그니처 검증): 설정된 클라이언트 중 prepare 가 빼지 않은 것. 공식 패치 서버 exe 로
    // 준비했으면 그때 받은 exe(snapshots\)와, 공식 패치 서버가 없는 지역(대만판)의 클라이언트
    public List<GameClient> ClientsUsed(Workspace ws, IEnumerable<GameClient> configured)
    {
        if (!FromServer) return configured.Where(c => !IsOutdated(c)).ToList();
        var used = new List<GameClient>();
        foreach (var (name, version) in GameVersions)
        {
            if (Region.Find(name) is not { } region || !Thaliak.Supports(region)) continue;
            string exe = Thaliak.CachedSnapshotExe(ws, region, version)
                ?? throw new ToolException($@"prepare 가 받아 둔 {region.Label} exe 가 없습니다 (snapshots\{region.Name}\{version}). prepare 를 다시 실행하세요.");
            used.Add(Prepare.ServerClient(region, exe, version));
        }
        used.AddRange(configured.Where(c => c.Region is { } r && !Thaliak.Supports(r)));
        return used;
    }

    // 이번 재료에서 뺀 클라이언트이고 그 뒤로 게임 버전이 그대로인지 (업데이트했으면 다시 prepare 해야 한다)
    public bool IsOutdated(GameClient c) =>
        c.Region != null && Outdated != null && Outdated.TryGetValue(c.Region.Name, out string? v) && v == c.Version;

    public static string? PluginVersionOf(string pluginPath) => Versions.OfAssembly(pluginPath)?.ToString();
}
