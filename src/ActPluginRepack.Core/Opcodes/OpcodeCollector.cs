// prepare 의 opcode 단계: 지역마다 출처를 골라 대조하고, Machina/Lemegeton/OverlayPlugin 형식으로 쓴다.
//
// 출처 우선순위 (지역마다):
//   1) Yarukon 공개본 — 게임 버전 날짜로 맞춘다 (CN/Global/KR 공통). IDA 로 항목마다 찾은 결과라 diff 보다 믿을 만하다.
//   2) karashiiro — 이번 버전용임이 확인될 때만 여기 (패치 번호를 GameVersions 로, 표에 없으면 지역 파일 갱신 시각을 출시
//      시각과 비교). 확인할 수 없으면(TC 등) 맨 뒤.
//   3) 로컬 opcodediff — 지금 exe 와 직전 출시 exe(Thaliak)를 맞대 만든 대응표로 베이스 플러그인 값을 바꾼다 (LocalOpcodediff).
//      패치 번호·대응표가 필요 없어 공개본이 나오기 전(출시 직후)에도 된다. 클라이언트가 있는 통합 지역(CN/Global/KR)에서 항상
//      돌려 공개본과 대조한다 (Python 이 없으면 휴대용을 받음; "localDiff": false 로 끔). 클라이언트가 없는 통합 지역은 같은
//      버전의 이 결과를 쓴다. vtable diff 는 큰 패치에서 가끔 틀리므로 1~2 와 다르면 1~2 를 쓴다.
//   4) opcodediff 공개 diff — 직전 버전 opcode 에 적용 (3 과 같은 스크립트라 독립된 확인은 아님)
//   5) IDA 직접 추출 — 1~4 가 없고 IDA 가 있을 때 (간편 모드에서는 쓰지 않음; --ida 면 항상, 가장 우선)
//   6) 확인되지 않은 karashiiro
// 고른 출처에 없는 이름은 다른 출처에서 보충하고, 출처끼리 값이 다르면 경고한다. 공개 자료(1·2·4) 없이 직접 구한 값만으로
// 정한 지역은 Unconfirmed 에 남긴다 (공개되면 다시 확인하도록 안내).
//
// 대상 지역: 모든 지역(Region.All).
//   - 이번에 쓰는 클라이언트(설정에 있고 최신 버전)의 지역은 Collect 로 — 위 출처 전부, 직접 추출 포함.
//   - 나머지 지역(클라이언트가 없거나, 최신 버전이 아니라 이번에 뺀 지역 — 보통 중국판·대만판, 경우에 따라 글로벌·한국도)은
//     Finish 로 — 직접 추출은 하지 않고 공개 출처나 같은 버전의 다른 클라이언트 추출 결과로만, Machina 26개가 다 채워질
//     때만 input\opcodes 에 쓴다 (일부만 바뀐 opcode 는 옛 값이 새 패킷과 겹칠 수 있어서). 못 구하면 예전 prepare 가 남긴
//     파일을 지워 베이스 플러그인 값을 쓰게 한다.
// idaFallback: 다른 출처가 모두 없을 때 IDA 를 돌릴지 (간편 모드는 false — IDA 는 고급 모드·명령줄에서만)
internal sealed class OpcodeCollector(Workspace ws, string pluginPath, AppConfig config, bool forceIda, bool idaFallback,
                                       bool overwrite,
                                       IReadOnlySet<string> sources) : IDisposable
{
    // --sources 로 쓸 출처를 제한할 수 있다 (검증용). 이름: yarukon / opcodediff / localdiff / ida / karashiiro
    public static readonly string[] SourceNames = ["yarukon", "opcodediff", "localdiff", "ida", "karashiiro"];
    private bool Use(string source) => sources.Contains(source);

    private readonly Dictionary<string, OpcodeSet?> _yarukon = new(StringComparer.Ordinal);
    private Dictionary<string, OpcodeSet>? _karashiiro;
    private readonly Dictionary<string, Dictionary<int, int>?> _opcodediff = new(StringComparer.Ordinal); // 패치 -> diff
    private readonly List<(string Version, OpcodeSet Set)> _derivedSets = []; // 직접 추출(localdiff/IDA) 결과와 그 게임 버전
    private readonly List<ResolvedOpcodes> _resolved = [];
    private readonly HashSet<Region> _unconfirmed = [];

    // 공개 자료 없이 직접 구한 값(로컬 opcodediff / IDA)으로만 정한 지역 — 큰 패치에서는 틀릴 수 있어 공개되면 다시 확인할 것
    public IReadOnlyCollection<Region> Unconfirmed => _unconfirmed;
    private Mono.Cecil.ModuleDefinition? _plugin;

    // 베이스 플러그인을 읽은 Cecil 모듈은 파일을 연 채로 둔다 — 취소·실패해도 닫히게 (Prepare 가 using)
    public void Dispose() => _plugin?.Dispose();

    // 지역별 결과: 고른 출처, 베이스 플러그인(Machina 의 <지역>.txt) 대비 값이 바뀐 이름 수, 출처에 없어 베이스 값을
    // 그대로 둔 이름 수. Missing 이 0 이고 Changed 가 0 이어야 "공식 플러그인 opcode 가 그대로 맞음"이다.
    public sealed record Outcome(string Source, int Changed, int Total, int Missing);
    private readonly Dictionary<Region, Outcome> _outcomes = [];
    public IReadOnlyDictionary<Region, Outcome> Outcomes => _outcomes;

    // 이 클라이언트의 opcode 를 확보해 썼으면 그 결과, 못 구했으면 null
    public Outcome? Collect(GameClient client)
    {
        _failures.Clear();
        var set = Resolve(client.Region!, client, client.Version!);
        if (set == null || !WriteMachina(client.Region!, set, requireComplete: false)) return null;
        _resolved.Add(new ResolvedOpcodes(client.Region!, client.Version, set));
        return _outcomes.GetValueOrDefault(client.Region!);
    }

    // 출처 하나의 실패(네트워크, 도구 없음, 형식 변경)는 그 출처만 건너뛰고 다음 출처로
    private T? Try<T>(string source, Func<T?> fetch) where T : class
    {
        try { return fetch(); }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Log.Warn($"[opcode] {source} 실패 - {ex.Message}");
            _failures.Add(ex);
            return null;
        }
    }

    private readonly List<Exception> _failures = [];

    // 마지막 Collect 에서 출처를 받지 못한 까닭 중 사용자에게 알릴 것 (Errors.DownloadFailure). 없으면 null —
    // 출처가 응답은 했는데 이번 버전 값이 없음 (아직 공개 전)
    public Exception? DownloadFailure => Errors.DownloadFailure(_failures);

    // 패치 번호 대응표가 필요했는데 받지 못했으면 그 까닭도 실패로 (표가 없어 건너뛴 것을 "아직 공개 전"으로 안내하지 않게)
    private void NoteVersionTableFailure()
    {
        if (GameVersions.LastFailure is { } f && !_failures.Contains(f)) _failures.Add(f);
    }

    // 설정에 없는 지역 + 부가 출력(extras\). 쓴 부가 출력 파일(extras\ 기준 경로)과 못 쓴 이유를 돌려준다.
    //   versionOf  지역의 지금 게임 버전 (공식 패치 서버 기준, 모르면 null — CnOpcodes 지역은 그때 건너뜀)
    //   outdated   설정에는 있지만 최신 버전이 아니라 이번에 뺀 클라이언트의 지역 (표시용)
    // 이번 버전 값을 못 구한 지역은 예전 prepare 가 남긴 input\opcodes 를 지운다 (옛 값으로 덮지 않고 베이스 값을 쓰게).
    public (List<string> Written, List<string> Skipped) Finish(Func<Region, string?> versionOf, IReadOnlySet<Region> outdated)
    {
        var written = new List<string>();
        var skipped = new List<string>();
        var configured = config.Clients.Select(c => c.Region!).ToHashSet();
        foreach (var region in Region.All.Where(r => !configured.Contains(r)))
        {
            string? version = region.CnOpcodes ? versionOf(region) : null;
            bool done = false;
            if (!region.CnOpcodes || version != null)
            {
                Log.Section($"[{region.Name}] ({(outdated.Contains(region) ? "클라이언트가 최신이 아니라 공개 자료만" : "클라이언트 설정 없음")}{(version != null ? $", {version}" : "")})");
                var set = Resolve(region, client: null, version);
                if (set != null && WriteMachina(region, set, requireComplete: true))
                {
                    _resolved.Add(new ResolvedOpcodes(region, version, set));
                    done = true;
                }
            }
            // 이번 값을 못 구했으면 예전 prepare 가 남긴 파일을 지운다 — 옛 값으로 덮지 않고 베이스 플러그인 값을 쓰게
            string stale = Path.Combine(ws.OpcodesDir, region.Opcodes + ".txt");
            if (!done && File.Exists(stale))
            {
                File.Delete(stale);
                Log.Note($@"[opcode] input\opcodes\{region.Opcodes}.txt 는 이번 게임 버전 것이 아니라 지움 - {region.Name} 은 베이스 플러그인 값을 씀");
            }
        }

        // 부가 출력도 이번 것만 남긴다 (만들지 못하면 지난 패치 것이 설치·배포되지 않게)
        foreach (string old in new[] { ws.LemegetonPath, ws.OverlayOpcodesPath })
            if (File.Exists(old)) File.Delete(old);
        if (_resolved.Count == 0)
        {
            skipped.Add("opcode 를 하나도 구하지 못해 extras 파일을 만들지 않았습니다.");
            return (written, skipped);
        }
        Log.Info();
        LemegetonOutput.Write(ws.LemegetonPath, _resolved);
        written.Add("lemegeton.xml");
        string? template = Try("OverlayPlugin 템플릿", () => Http.GetStringOrNull(OverlayPluginOutput.UpstreamUrl));
        if (template == null)
        {
            Log.Warn("[overlayplugin] OverlayPlugin 저장소의 opcodes.jsonc 를 받지 못해 생략");
            skipped.Add("OverlayPlugin 원본 opcodes.jsonc 를 받지 못해 OverlayPlugin 용 파일은 만들지 않았습니다.");
        }
        else if (Try<object>("OverlayPlugin 출력", () =>
                 {
                     OverlayPluginOutput.Write(ws.OverlayOpcodesPath, template, _resolved);
                     return written; // 성공 표시
                 }) != null)
            written.Add(@"OverlayPlugin\opcodes.jsonc");
        else skipped.Add("OverlayPlugin 용 파일을 만들지 못했습니다.");
        return (written, skipped);
    }

    private OpcodeSet? Resolve(Region region, GameClient? client, string? version)
    {
        bool cn = region.CnOpcodes && version != null;
        OpcodeSet? yarukon = cn && Use("yarukon") ? Try("Yarukon", () => Yarukon(version!)) : null;
        OpcodeSet? opcodediff = cn && Use("opcodediff") ? Try("opcodediff", () => Opcodediff(region, version!)) : null;
        var k = Use("karashiiro") ? Try("karashiiro", () => Karashiiro(region, version)) : null;
        OpcodeSet? karashiiro = k?.Set;
        OpcodeSet? karashiiroFirst = k is { Verified: true } ? karashiiro : null;
        // 이번 버전 것인지 확인되지 않은 karashiiro 는 클라이언트가 설정된 지역에서만, 다른 출처가 없을 때 쓴다
        // (설정 없는 지역(대만판 등)에 옛 패치 값을 넣어 공식 플러그인의 새 값을 덮지 않게)
        OpcodeSet? karashiiroLast = k is { Verified: false } && client != null ? karashiiro : null;
        bool published = yarukon != null || opcodediff != null || karashiiroFirst != null;

        // 로컬 opcodediff (~5초, 결과는 버전마다 캐시): 클라이언트가 있는 통합 지역이면 항상
        OpcodeSet? localdiff = null;
        if (client != null && region.CnOpcodes && config.LocalDiff && Use("localdiff"))
        {
            if (!LocalDiff.HasPython(config.Python))
                Log.Warn($"[opcode] 로컬 opcodediff 는 설정한 Python 경로({config.Python})가 없어 건너뜀");
            else
            {
                localdiff = Try("로컬 opcodediff", () => LocalOpcodediff(client));
                if (localdiff != null) _derivedSets.Add((version!, localdiff));
            }
        }
        // IDA (~5분): --ida 면 항상, 아니면 다른 출처가 모두 없을 때만 (간편 모드는 그때도 쓰지 않음)
        OpcodeSet? ida = null;
        if (client != null && config.Ida != null && Use("ida") && (forceIda || (idaFallback && !published && localdiff == null)))
        {
            if (!forceIda) Log.Note("[opcode] 로컬 opcodediff·공개본 없음 - IDA 로 직접 추출");
            ida = Try("IDA", () => RunIda(client));
            if (ida != null) _derivedSets.Add((version!, ida));
        }
        // 설정에 없는 지역: 같은 opcode 를 쓰는 다른 클라이언트의 같은 게임 버전 직접 추출 결과
        OpcodeSet? derived = client == null && region.CnOpcodes
            ? _derivedSets.FirstOrDefault(d => d.Version == version).Set : null;

        OpcodeSet? primary =
            forceIda ? ida ?? yarukon ?? karashiiroFirst ?? localdiff ?? derived ?? opcodediff ?? karashiiroLast
            : yarukon ?? karashiiroFirst ?? localdiff ?? derived ?? opcodediff ?? ida ?? karashiiroLast;
        if (primary != null && !published && (primary == localdiff || primary == derived || primary == ida))
            _unconfirmed.Add(region);
        if (primary == null)
        {
            if (client == null)
            {
                // 클라이언트가 없는(또는 뺀) 지역은 직접 추출하지 않는다. 예전 파일은 Finish 가 지우고 베이스 값을 쓴다.
                Log.Warn($"[opcode] {region.Opcodes}: 이번 버전 것으로 확인된 출처 없음 - 베이스 플러그인 값을 씀 (클라이언트가 없는 지역은 직접 추출하지 않음)");
                return null;
            }
            string hint = config.Ida != null && !idaFallback
                ? ", 고급 모드에서 IDA 로 직접 추출할 수 있음" : "";
            Log.Warn($@"[opcode] {region.Opcodes}: 쓸 수 있는 출처 없음 - input\opcodes\{region.Opcodes}.txt 는 그대로" +
                    $" (나중에 다시 실행{hint})");
            return null;
        }

        var merged = new Dictionary<string, int>(primary.Server, StringComparer.Ordinal);
        var others = new[] { yarukon, opcodediff, localdiff, ida, derived, karashiiro }
            .Where(s => s != null && s != primary).Select(s => s!).Distinct().ToList();
        foreach (var other in others) CrossCheck(region, primary, other);
        var filled = new List<string>();
        // 보충은 이번 버전 것으로 확인된 출처에서만 (확인되지 않은 karashiiro 는 대조에만 쓴다)
        var fillers = others.Where(o => o != karashiiro || k is { Verified: true }).ToList();
        foreach (string name in OpcodeNames.Used.Where(n => !merged.ContainsKey(n)))
        {
            var from = fillers.FirstOrDefault(o => o.Server.ContainsKey(name));
            if (from == null) continue;
            merged[name] = from.Server[name];
            filled.Add($"{name}({from.Source})");
        }
        if (filled.Count > 0) Log.Warn($"[opcode] {primary.Source} 에 없어 보충: {string.Join(", ", filled)}");

        // 패치 번호(7.57 등) 이름표: 대응표에서, 없으면 값이 같은 karashiiro 에서
        bool karashiiroAgrees = karashiiro != null && OpcodeNames.Machina.All(m =>
            karashiiro.Server.TryGetValue(m.Name, out int v) && merged.TryGetValue(m.Name, out int w) && v == w);
        string? label = (cn ? GameVersions.RetailOf(version!) : null) ?? (karashiiroAgrees ? karashiiro!.PatchLabel : null);
        return new OpcodeSet(primary.Source + (filled.Count > 0 ? " +보충" : ""), merged, label);
    }

    // IDA 직접 추출. 통합 클라이언트(CN/글로벌/한국 — 같은 버전이면 opcode 가 같다)는 기본으로 같은 버전의 중국판 exe 를
    // 분석한다(config "idaTarget"): finder 의 시그니처가 중국판 기준이라 보정 없이 맞고, 한 번 분석한 결과를 다른 지역에도
    // 쓴다. 대상 지역 클라이언트가 설정에 있으면 그 exe, 없으면 공식 패치 서버에서 받는다 (받지 못하면 이 클라이언트 exe 로).
    // 대만판처럼 opcode 가 다른 지역은 그 클라이언트 exe 그대로.
    private readonly Dictionary<string, OpcodeSet> _ida = new(StringComparer.OrdinalIgnoreCase); // 작업 폴더 -> 결과

    // IDA 로 분석할 (지역, exe). download: 공식 패치 서버에서 그 지역·버전의 exe 를 받아 경로를 돌려준다.
    internal static (Region Target, string Exe) IdaTarget(GameClient client, AppConfig config, Func<Region, string, string> download)
    {
        string version = client.Version!;
        if (!client.Region!.CnOpcodes || Region.Find(config.IdaTarget) is not { CnOpcodes: true } wanted || wanted == client.Region)
            return (client.Region, client.ExePath);
        if (config.Clients.FirstOrDefault(c => c.Region == wanted && c.Version == version) is { } local)
            return (wanted, local.ExePath);
        if (Thaliak.Supports(wanted))
        {
            try { return (wanted, download(wanted, version)); }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                Log.Warn($"[opcode] IDA: {wanted.Name} {version} exe 를 받지 못해 {client.Name} exe 로 분석 - {Errors.Describe(ex)}");
            }
        }
        return (client.Region, client.ExePath);
    }
    private OpcodeSet RunIda(GameClient client)
    {
        string version = client.Version!;
        var (target, exe) = IdaTarget(client, config, (region, v) => Thaliak.SnapshotExe(ws, region, v));
        string workDir = Ida.WorkDir(ws, target, version);
        if (_ida.TryGetValue(workDir, out var done))
        {
            Log.Note($"[opcode] IDA: 같은 버전 {target.Name} exe 분석 결과 사용");
            return done;
        }
        if (target != client.Region) Log.Note($"[opcode] IDA: 같은 버전의 {target.Name} exe 로 분석 (opcode 동일)");
        return _ida[workDir] = Ida.FindOpcodes(Ida.ResolveIdat(config.Ida!), workDir, exe, target, overwrite);
    }

    // opcodediff 공개 diff(직전 버전 -> 이번 버전)
    private OpcodeSet? Opcodediff(Region region, string version)
    {
        string? retail = GameVersions.RetailOf(version);
        var prev = GameVersions.Previous(version);
        if (retail == null || prev == null)
        {
            NoteVersionTableFailure();
            return null;
        }
        if (!_opcodediff.TryGetValue(retail, out var map)) _opcodediff[retail] = map = OpcodeSources.FetchOpcodediff(retail);
        return map == null ? null : ApplyDiff(region, map, retail, prev, $"opcodediff {retail}");
    }

    // opcodediff 로컬 실행 (config.json "localDiff" — 기본 켜짐, false 면 끔): 지금 exe 와 직전 출시 exe(Thaliak 출시 목록)를
    // 맞대 "옛 값 -> 새 값" 대응표를 만들고, 베이스 플러그인 값을 거기 비춰 본다 (JudgeLocalDiff). 패치 번호는 쓰지 않는다
    // (스크립트는 exe 파일 이름을 읽지 않음). 공식 플러그인은 항상 최신 출시용이거나 그 직전 출시용이라 직전 출시 하나면 되지만,
    // 안전장치로 맞지 않으면 한 출시 더 앞의 exe 와도 맞대 본다 (거기서는 "베이스가 그 옛 출시용"일 때만 받아들인다).
    private const int LocalDiffReleases = 2;

    private OpcodeSet? LocalOpcodediff(GameClient client)
    {
        var region = client.Region!;
        string version = client.Version!;
        var olds = Thaliak.EarlierReleases(region, version).Take(LocalDiffReleases).ToList();
        if (olds.Count == 0)
        {
            Log.Warn($"[opcode] localdiff: 공식 패치 목록에 {version} 앞의 출시가 없어 생략");
            return null;
        }
        var baseValues = BaseMachina(region);
        for (int i = 0; i < olds.Count; i++)
        {
            string prev = olds[i];
            var map = LocalDiff.Run(ws, client, prev, config.Python, overwrite);
            string source = $"localdiff {region.Name} {prev} -> {version}";
            var judged = JudgeLocalDiff(baseValues, map);
            if (judged.Verdict == LocalDiffVerdict.BaseIsPrevious)
                Log.Note($"[opcode] {source}: 베이스 플러그인은 {(i == 0 ? "직전 출시용" : "이 옛 출시용")} - 대응표로 바꿈");
            else if (judged.Verdict == LocalDiffVerdict.BaseIsCurrent && i == 0)
                Log.Note($"[opcode] {source}: 베이스 플러그인은 이미 지금 버전용");
            else
            {
                Log.Warn($"[opcode] {source}: 베이스 플러그인 값이 대응표와 맞지 않음 (옛 값 {judged.OldHits}/{judged.Total}, " +
                         $"새 값 {judged.NewHits}/{judged.Total})" + (i + 1 < olds.Count ? " - 한 출시 더 앞과 맞대 봄" : " - 이 결과는 쓰지 않음"));
                continue;
            }
            return WithYarukonExtras(judged.Server!, map, prev, source, version);
        }
        return null;
    }

    private OpcodeSet WithYarukonExtras(Dictionary<string, int> server, Dictionary<int, int> map, string prev, string source, string version)
    {
        // 베이스 플러그인에 없는 이름(부가 출력용)은 맞댄 옛 출시의 Yarukon 목록이 대응표의 옛 값과 맞을 때 거기서 바꿔 채운다
        if (Use("yarukon") && Try($"Yarukon {prev}", () => Yarukon(prev)) is { } prevList)
        {
            var used = prevList.Server.Where(kv => OpcodeNames.Used.Contains(kv.Key)).ToList();
            if (used.Count > 0 && used.Count(kv => map.ContainsKey(kv.Value)) >= used.Count * 0.9)
                foreach (var (name, old) in used)
                    if (!server.ContainsKey(name) && map.TryGetValue(old, out int now)) server[name] = now;
        }
        return new OpcodeSet(source, server, GameVersions.RetailOf(version));
    }

    internal enum LocalDiffVerdict { BaseIsPrevious, BaseIsCurrent, Unknown }

    internal sealed record LocalDiffJudgement(LocalDiffVerdict Verdict, Dictionary<string, int>? Server, int OldHits, int NewHits, int Total);

    // 직전 출시 -> 지금 대응표(map)에 베이스 플러그인 값(이름 -> 값)을 비춰 본다. opcode 공간이 촘촘해서(~1024 중 ~775 사용) 값
    // 하나는 우연히도 어느 쪽에든 있으므로 전부 맞아야 인정한다 (우연히 26개가 다 맞을 확률은 0.1% 미만).
    //   전부 옛 값 쪽에 있음 -> 베이스는 직전 출시용: 대응표로 바꾼 값 (바꿔도 같으면 opcode 가 안 바뀐 출시)
    //   전부 새 값 쪽에만 있음 -> 베이스는 이미 지금 버전용: 베이스 값 그대로
    //   어느 쪽도 아님, 또는 양쪽 다 맞는데 바꾼 값이 베이스와 다름(판단 불가) -> Unknown
    // 대응표 오류를 "이미 지금 버전용"으로 넘겨짚지 않는다 — 패치가 필요한데 아무것도 하지 않게 되므로.
    internal static LocalDiffJudgement JudgeLocalDiff(IReadOnlyDictionary<string, int> baseValues, IReadOnlyDictionary<int, int> map)
    {
        var used = baseValues.Where(kv => OpcodeNames.Used.Contains(kv.Key)).ToList();
        var newSide = map.Values.ToHashSet();
        int oldHits = used.Count(kv => map.ContainsKey(kv.Value));
        int newHits = used.Count(kv => newSide.Contains(kv.Value));
        bool onOld = used.Count > 0 && oldHits == used.Count, onNew = used.Count > 0 && newHits == used.Count;
        var mapped = onOld
            ? baseValues.Where(kv => map.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => map[kv.Value], StringComparer.Ordinal)
            : null;
        bool unchanged = mapped != null && used.All(kv => mapped[kv.Key] == kv.Value);
        if (onOld && (!onNew || unchanged)) return new(LocalDiffVerdict.BaseIsPrevious, mapped, oldHits, newHits, used.Count);
        if (onNew && !onOld) return new(LocalDiffVerdict.BaseIsCurrent, new(baseValues, StringComparer.Ordinal), oldHits, newHits, used.Count);
        return new(LocalDiffVerdict.Unknown, null, oldHits, newHits, used.Count);
    }

    // 베이스 플러그인의 Machina opcode (이름 -> 값)
    private Dictionary<string, int> BaseMachina(Region region) =>
        OpcodeNames.Machina
            .Join(MachinaOpcodes.Parse(BaseText(region), "base"), m => m.Out, kv => kv.Key, (m, kv) => (m.Name, kv.Value))
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

    // diff(직전 버전 opcode -> 이번 버전 opcode)를 직전 버전 opcode 목록에 적용한다.
    // 직전 버전 목록: Yarukon 의 직전 버전 전체 목록, 없으면 베이스 플러그인(직전 버전용이라고 보고).
    // 어느 쪽이든 그 opcode 가 diff 의 "old" 쪽에 거의 다 있어야 같은 버전으로 인정한다.
    private OpcodeSet? ApplyDiff(Region region, Dictionary<int, int> map, string retail, GameVersions.Entry prev, string source)
    {
        // 인정 기준: opcode 공간이 촘촘해서(~1024 중 ~775 사용) 엉뚱한 버전 값도 꽤 맞는다. 베이스(26개)는
        // 전부, Yarukon(약 38개)은 90% 이상 맞아야 한다 — 우연히 통과할 확률이 1% 미만.
        var candidates = new List<(string What, Dictionary<string, int> Old, double Need)>();
        // 직전 버전 Yarukon 을 받지 못해도 베이스 플러그인 후보로는 해 본다 (diff 결과를 통째로 버리지 않게)
        if (Use("yarukon") && Try($"Yarukon {prev.Retail}", () => Yarukon(prev.Version)) is { } prevSet)
            candidates.Add(($"Yarukon {prev.Retail}", prevSet.Server, 0.9));
        candidates.Add(("베이스 플러그인", BaseMachina(region), 1.0));

        foreach (var (what, old, need) in candidates)
        {
            var used = old.Where(kv => OpcodeNames.Used.Contains(kv.Key)).ToList();
            int hit = used.Count(kv => map.ContainsKey(kv.Value));
            if (used.Count == 0 || hit < used.Count * need)
            {
                Log.Note($"[opcode] {source}: {what} 가 직전 버전({prev.Retail}) 값이 아닌 듯 ({hit}/{used.Count}) - 건너뜀");
                continue;
            }
            var server = old.Where(kv => map.ContainsKey(kv.Value))
                .ToDictionary(kv => kv.Key, kv => map[kv.Value], StringComparer.Ordinal);
            return new OpcodeSet(source, server, retail);
        }
        return null;
    }

    private static void CrossCheck(Region region, OpcodeSet a, OpcodeSet b)
    {
        var diff = OpcodeNames.Used
            .Where(n => a.Server.TryGetValue(n, out int x) && b.Server.TryGetValue(n, out int y) && x != y)
            .Select(n => $"{n}: {a.Server[n]} vs {b.Server[n]}").ToList();
        int compared = OpcodeNames.Used.Count(n => a.Server.ContainsKey(n) && b.Server.ContainsKey(n));
        if (diff.Count == 0) Log.Note($"[opcode] {region.Opcodes}: {a.Source} = {b.Source} ({compared}개 대조 일치)");
        else Log.Warn($"[opcode] {region.Opcodes}: {a.Source} 와 {b.Source} 가 다름:\n    " + string.Join("\n    ", diff));
    }

    private OpcodeSet? Yarukon(string version)
    {
        string folder = OpcodeSources.YarukonFolder(version);
        if (!_yarukon.TryGetValue(folder, out var set)) _yarukon[folder] = set = OpcodeSources.FetchYarukon(version);
        return set;
    }

    // Verified: 이번 게임 버전 것임이 확인됨 (아니면 다른 출처가 없을 때만 쓴다)
    private sealed record KarashiiroSet(OpcodeSet Set, bool Verified);

    private KarashiiroSet? Karashiiro(Region region, string? version)
    {
        _karashiiro ??= OpcodeSources.FetchKarashiiro();
        if (!_karashiiro.TryGetValue(region.Karashiiro, out var set)) return null;

        // 패치 번호를 게임 버전으로 바꿀 수 있으면 정확히 맞춘다 (대응표는 글로벌 기준 - TC 제외)
        string? mapped = region.CnOpcodes && set.PatchLabel != null ? GameVersions.VersionOf(set.PatchLabel) : null;
        if (mapped != null && version != null)
        {
            if (mapped == version) return new(set, true);
            Log.Note($"[opcode] {set.Source}: {mapped} 용이라 클라이언트({version})와 다름 - 제외");
            return null;
        }

        // 반대 방향으로도: 클라이언트 버전의 패치 번호를 알면 karashiiro 의 패치 번호와 같아야 한다
        // (karashiiro 쪽 번호가 대응표에 아직 없을 때 — 예전 패치 데이터를 이번 것으로 받아들이지 않게)
        if (region.CnOpcodes && version != null && set.PatchLabel != null && GameVersions.RetailOf(version) is { } retail)
        {
            if (retail.Equals(set.PatchLabel, StringComparison.OrdinalIgnoreCase)) return new(set, true);
            Log.Note($"[opcode] {set.Source}: 패치 {set.PatchLabel} 용이라 클라이언트({retail})와 다름 - 제외");
            return null;
        }

        // 패치 번호가 대응표에 아직 없으면: 이 지역 데이터 파일이 이번 출시를 내려준 뒤에 갱신됐는지로 (한 파일에 모든 지역이
        // 모이므로 지역별 원본 파일의 커밋 시각 — 대만판만 갱신한 커밋을 이 지역 갱신으로 보지 않게)
        if (version != null && UpdatedSinceRelease(region, version) is { } updated)
        {
            if (updated)
            {
                Log.Note($"[opcode] {set.Source}: 이번 출시 뒤에 갱신됨 - 이번 버전 것으로 봄");
                return new(set, true);
            }
            Log.Note($"[opcode] {set.Source}: 이번 출시 전에 갱신된 것 - 제외");
            return null;
        }

        // 그것도 알 수 없으면(TC 등): 베이스 플러그인 값과 대부분 같으면 아직 이번 패치로 갱신되지
        // 않은 것. 달라도 직전 패치 데이터일 수 있으므로 확인되지 않았다고 알린다.
        var baseValues = MachinaOpcodes.Parse(BaseText(region), "base");
        var mine = MachinaOpcodes.FromSet(set);
        int compared = mine.Count(kv => baseValues.ContainsKey(kv.Key));
        int same = mine.Count(kv => baseValues.TryGetValue(kv.Key, out int v) && v == kv.Value);
        if (compared > 0 && same * 2 >= compared)
        {
            Log.Note($"[opcode] {set.Source}: 베이스 플러그인과 {same}/{compared} 같음 - 아직 미갱신으로 보고 제외");
            return null;
        }
        Log.Warn($"[opcode] {set.Source}: 이번 게임 버전 것인지 확인할 수 없음 (패치 번호 대응표에 없음) - 다른 출처가 없을 때만 쓰임");
        return new(set, false);
    }

    // karashiiro 의 이 지역 원본 파일이 이번 버전이 나온 뒤(Thaliak 이 처음 내려준 시각)에 커밋됐는지. 알 수 없으면 null.
    // 통합 클라이언트(CN/Global/KR)는 같은 버전을 쓰므로 어느 지역에서든 처음 나온 시각 — 한국 서버가 몇 시간 늦게 패치돼도
    // 그 전에 올라온 이번 버전 데이터를 옛 것으로 보지 않게 (7.57: 글로벌 05:51, karashiiro 06:25, 한국 08:15).
    private static bool? UpdatedSinceRelease(Region region, string version)
    {
        if (OpcodeSources.KarashiiroFile(region) is not { } file) return null;
        try
        {
            var regions = region.CnOpcodes ? Region.All.Where(r => r.CnOpcodes) : [region];
            if (regions.Select(r => Thaliak.ReleasedAt(r, version)).Where(t => t != null).Min() is not { } released) return null;
            var commit = GitHub.RecentCommits(OpcodeSources.KarashiiroRepo, "master", file, 1).FirstOrDefault();
            return commit == null ? null : commit.Date >= released;
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Log.Note($"[opcode] karashiiro 갱신 시각을 확인하지 못함 - {Errors.Describe(ex)}");
            return null;
        }
    }

    private string BaseText(Region region)
    {
        if (!File.Exists(pluginPath)) throw new ToolException($"플러그인 파일이 없습니다: {pluginPath}");
        _plugin ??= Mono.Cecil.ModuleDefinition.ReadModule(pluginPath);
        return MachinaOpcodes.ReadBase(_plugin, region.Opcodes);
    }

    // 썼으면 true.
    private bool WriteMachina(Region region, OpcodeSet set, bool requireComplete)
    {
        string dst = Path.Combine(ws.OpcodesDir, region.Opcodes + ".txt");
        string rel = $@"input\opcodes\{region.Opcodes}.txt";
        string baseText = BaseText(region);
        var merge = MachinaOpcodes.Apply(baseText, MachinaOpcodes.FromSet(set));
        // 베이스에 있지만 이 도구가 모르는 이름(공식 플러그인이 새로 추가한 것)은 출처에서 구할 수 없다 — 베이스 값을 두고
        // 크게 알린다. "출처에 빠진 이름"과 섞으면 판단이 영영 "알 수 없음"이 된다.
        var known = OpcodeNames.Machina.Select(m => m.Out).ToHashSet(StringComparer.Ordinal);
        var unknown = merge.Missing.Where(n => !known.Contains(n)).ToList();
        var missing = merge.Missing.Where(known.Contains).ToList();
        if (unknown.Count > 0)
            Log.Warn($"[opcode] 이 도구가 모르는 이름이 베이스 플러그인에 있음 - 베이스 값 유지 (도구 업데이트 필요): {string.Join(", ", unknown)}");
        if (missing.Count > 0)
        {
            if (requireComplete)
            {
                Log.Warn($"[opcode] {set.Source} 에 {missing.Count}개 없음 - {rel} 생략 ({string.Join(", ", missing)})");
                return false;
            }
            Log.Warn($"[opcode] 값이 없어 베이스 값 유지: {string.Join(", ", missing)}");
        }
        // 서버 opcode 는 이름마다 다르다 — 같은 값이 둘이면(베이스에 남은 옛 값이 새 값과 겹침 등) 패킷을 잘못 해석한다
        var dup = MachinaOpcodes.Parse(merge.Text, rel).GroupBy(kv => kv.Value).Where(g => g.Count() > 1)
            .Select(g => string.Join("=", g.Select(kv => kv.Key)) + $"({g.Key:X})").ToList();
        if (dup.Count > 0)
        {
            Log.Warn($"[opcode] {set.Source}: 서로 다른 이름이 같은 opcode 를 가리킴 - {rel} 생략 ({string.Join(", ", dup)})");
            return false;
        }

        // BOM 은 베이스 원문에 있으면 문자(U+FEFF)로 남아 있어 그대로 다시 쓰인다
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(merge.Text);
        bool same = File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(bytes);
        Files.WriteAtomic(dst, bytes);
        int total = MachinaOpcodes.Parse(baseText, "base").Count;
        _outcomes[region] = new Outcome(set.Source, merge.Changed, total, missing.Count);
        Log.Info($"[opcode] {rel} <- {set.Source}  (베이스 대비 {merge.Changed}/{total} 변경" +
                          (same ? ", 기존 파일과 동일)" : ")"));
        return true;
    }
}
