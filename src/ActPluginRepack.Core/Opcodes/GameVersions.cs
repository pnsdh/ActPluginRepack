using System.Text.Json;

// 패치 번호("7.57") <-> 게임 버전("2026.10.06.0000.0000") 대응표 (글로벌 클라이언트 기준).
// 출처: xivdev/opcodediff 의 automation/ffxiv_versions_global.json (패치마다 자동 갱신, 시간순).
// 7.5 이후 CN/KR 도 같은 버전 문자열을 쓰지만 TC 는 다르므로 TC 에는 쓰지 않는다.
//
// GUI 는 며칠씩 켜 두기도 하므로(패치 당일을 넘김) 표를 영원히 들고 있지 않는다:
//   - 받은 지 FreshFor 가 지나면 다시 받는다 (HttpCache 가 ETag 로 묻기만 하므로 바뀌지 않았으면 가볍다)
//   - "캐시 무시"(HttpCache.Bypass) 작업이면 잠깐(30초) 안에 받은 것이 아니면 다시 받는다
//   - 표에 없는 버전/패치 번호를 찾으면 한 번 다시 받아 본다 (FreshFor 에 한 번까지 — 표에 없는 중간 패치가 흔하므로)
// 받지 못하면 갖고 있던 표를 그대로 쓴다. 새 prepare 를 시작하면(Http.ConnectionEpoch) 위의 "다시 받지 않음" 기억을
// 잊는다 — 인터넷을 다시 연결한 뒤의 실행이 조금 전 실패 때문에 빈 표로 "아직 공개 전"이라고 안내하지 않게.
internal static class GameVersions
{
    // 시험에서만 바꾼다
    internal static string Url { get; set; } = "https://raw.githubusercontent.com/xivdev/opcodediff/master/automation/ffxiv_versions_global.json";
    private static readonly TimeSpan FreshFor = HttpCache.FreshFor;
    private static readonly TimeSpan FreshForBypass = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    public sealed record Entry(string Retail, string Version);

    private static readonly Lock Gate = new();
    private static List<Entry>? _all;
    private static DateTime _fetchedAt, _failedAt, _missRefreshAt;
    private static readonly EpochWatch RunEpoch = new();
    private static Exception? _lastFailure;

    // 이번 작업(ConnectionEpoch)에서 표를 받지 못한 까닭 (그 뒤에 받았거나 새 작업이면 null). 표에 없어 못 찾은 것이
    // "아직 공개 전"인지 "표를 받지 못해서"인지 쓰는 쪽(OpcodeCollector 등)이 구분하게.
    public static Exception? LastFailure
    {
        get { lock (Gate) { ForgetIfNewRun(); return _lastFailure; } }
    }

    public static IReadOnlyList<Entry> All => Load(refresh: false);

    // 시험용: 받은 표와 기억을 처음처럼
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _all = null;
            _fetchedAt = _failedAt = _missRefreshAt = default;
            _lastFailure = null;
        }
    }

    // 새 작업이 시작됐으면 실패·다시 받기 기억을 잊는다 (Gate 안에서)
    private static void ForgetIfNewRun()
    {
        if (!RunEpoch.Changed()) return;
        _failedAt = _missRefreshAt = default;
        _lastFailure = null;
    }

    private static IReadOnlyList<Entry> Load(bool refresh)
    {
        lock (Gate)
        {
            ForgetIfNewRun();
            var now = DateTime.UtcNow;
            if (_all != null && !refresh && now - _fetchedAt < (HttpCache.Bypass ? FreshForBypass : FreshFor)) return _all;
            // 실패는 캐시하지 않는다 (오래 떠 있는 프로세스에서 한 번의 네트워크 오류로 계속 비지 않게) — 단 5분 동안은 다시 안 받음
            if (now - _failedAt < RetryAfterFailure) return _all ?? [];
            try
            {
                string json = Http.GetString(Url);
                using var doc = JsonDocument.Parse(json);
                var list = doc.RootElement.EnumerateArray()
                    .Select(e => new Entry(
                        e.GetProperty("retail_version").GetString() ?? throw new ToolException($"패치 번호 대응표에 패치 번호가 없는 항목이 있습니다 ({Url})."),
                        e.GetProperty("version_string").GetString() ?? throw new ToolException($"패치 번호 대응표에 게임 버전이 없는 항목이 있습니다 ({Url}).")))
                    .ToList();
                _fetchedAt = now;
                _lastFailure = null;
                return _all = list;
            }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                _failedAt = now;
                _lastFailure = ex;
                Log.Warn(_all != null
                    ? $"[version] 패치 번호 대응표를 새로 받지 못함 ({ex.Message}) - 받아 둔 표 사용"
                    : $"[version] 패치 번호 대응표를 받지 못함 ({ex.Message}) - 버전 확인은 추정으로");
                return _all ?? [];
            }
        }
    }

    // 표에 없으면 한 번 다시 받아 본다 (패치 당일 GUI 를 켜 둔 채로 새 버전을 찾는 경우)
    private static IReadOnlyList<Entry> Find(Func<Entry, bool> match)
    {
        var all = All;
        if (all.Any(match)) return all;
        lock (Gate)
        {
            ForgetIfNewRun();
            if (DateTime.UtcNow - _missRefreshAt < FreshFor || DateTime.UtcNow - _fetchedAt < TimeSpan.FromSeconds(5)) return all;
            _missRefreshAt = DateTime.UtcNow;
        }
        return Load(refresh: true);
    }

    public static string? RetailOf(string version) =>
        Find(e => e.Version == version).LastOrDefault(e => e.Version == version)?.Retail;

    public static string? VersionOf(string retail) =>
        Find(e => e.Retail.Equals(retail, StringComparison.OrdinalIgnoreCase))
            .LastOrDefault(e => e.Retail.Equals(retail, StringComparison.OrdinalIgnoreCase))?.Version;

    // version 바로 앞 항목 (없으면 null)
    public static Entry? Previous(string version)
    {
        var all = Find(e => e.Version == version);
        int i = all.ToList().FindLastIndex(e => e.Version == version);
        return i > 0 ? all[i - 1] : null;
    }
}
