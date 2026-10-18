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
    PrepareOutcome? Outcome = null)
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
            else if (prepared != c.Version)
                diffs.Add($"{c.Name}: 마지막 prepare 이후 게임이 패치되었습니다 ({prepared} -> {c.Version}).");
        }
        return diffs;
    }

    // 이번 재료에서 뺀 클라이언트이고 그 뒤로 게임 버전이 그대로인지 (업데이트했으면 다시 prepare 해야 한다)
    public bool IsOutdated(GameClient c) =>
        c.Region != null && Outdated != null && Outdated.TryGetValue(c.Region.Name, out string? v) && v == c.Version;

    public static string? PluginVersionOf(string pluginPath) => Versions.OfAssembly(pluginPath)?.ToString();
}
