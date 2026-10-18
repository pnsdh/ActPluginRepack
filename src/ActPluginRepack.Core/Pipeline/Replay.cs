using System.Diagnostics;
using System.Text.Json.Nodes;

// 지난 패치 재현 (개발용 자동 시험) — 패치 당일 개발자 없이도 이 도구가 맞게 고치는지 확인한다.
// 지난 출시를 "공개 opcode 자료가 아직 없는 패치 당일 오전" 조건으로 다시 세워 prepare -> build 를 돌리고, 결과를 그 출시 뒤에
// 나온 공식 플러그인(정답)과 비교한다.
//   출시 시각 T  Thaliak 이 그 출시를 처음 내려준 시각 (first_offered)
//   베이스       T 전에 게시된 마지막 공식 릴리스 (패치 당일 오전의 "아직 업데이트되지 않은 플러그인")
//   정답         T 뒤, 다음 출시 전에 처음 게시된 공식 릴리스. 다음 출시까지 새 릴리스가 없으면 베이스가 정답(공식이 고칠 필요가
//                없었음), 다음 출시도 아직 없으면 정답 없음(공식이 아직 안 나옴 — 비교 없이 결과만)
//   작업 폴더    <work>\input\plugin = 베이스, ref\ = 정답, game\game\ffxiv_dx11.exe = 그 버전 exe(공식 패치 서버, 서명 확인),
//                config.json = IDA 끔 · localDiff 켬 · 그 클라이언트 하나. 받은 파일 기억(HttpCache)도 <work>\cache\http 에
//                (사용자 폴더 %LOCALAPPDATA%\ActPluginRepack 에 쓰지 않게). tools\ · snapshots\ · dl\ · cache\ 는 다음 실행이 다시 쓴다.
//   출처         기본 localdiff + sigregen — 이 PC 에서 구하는 것만 (공개 저장소·upstream deucalion 은 "지금"의 자료라 당일 오전과 다르다)
// 비교: opcode(그 지역), 판단(패치가 필요한지), 후크(정답과 같은 곳을 가리키는지), sigcheck.
internal sealed record ReplayOptions
{
    public static readonly string[] DefaultSources = ["localdiff", "sigregen"];

    public Region Region { get; init; } = Region.Global;
    public required string Version { get; init; }  // 그 지역의 한 출시의 게임 버전
    public string? BaseTag { get; init; }           // 베이스 공식 릴리스 (없으면 출시 시각으로)
    public string? RefTag { get; init; }            // 정답 공식 릴리스 (없으면 출시 시각으로)
    public IReadOnlyCollection<string> Sources { get; init; } = DefaultSources;
    public string? WorkDir { get; init; }           // 기본 %TEMP%\actrepack-replay\<지역>-<버전>
    public string? ToolsDir { get; init; }          // 받아 둔 python / radare2 / opcodediff-venv 가 있는 tools 폴더 (복사해 씀)
    public bool Keep { get; init; }                 // 끝나도 작업 폴더를 남김
}

// 비교 항목 하나. Details: 다른 것들
internal sealed record ReplayCheck(string Name, bool Pass, string Summary, IReadOnlyList<string> Details)
{
    // (이름 칸은 StatusLine 처럼 한글을 두 칸으로 세어 맞춘다)
    public string Line() => $"  {(Pass ? "OK" : "!!"),-4} {StatusLine.Format(Name, Summary, 19)}";
}

// Reference: 정답 태그 (null: 정답 없음 — 비교하지 않음), ReferenceIsBase: 다음 출시까지 새 공식 릴리스가 없어 베이스를 정답으로 봄
internal sealed record ReplayResult(Region Region, string Version, string? Previous, DateTime ReleasedAt, string BaseTag, string? Reference,
                                    bool ReferenceIsBase, PrepareOutcome Outcome, string Verdict, IReadOnlyList<StepResult> Steps, bool Built,
                                    IReadOnlyList<ReplayCheck> Checks, TimeSpan Elapsed, string WorkDir, bool Kept)
{
    public bool Compared => Reference != null;
    public bool Pass => Checks.All(c => c.Pass);
}

internal static class Replay
{
    private const string Marker = ".actrepack-replay";
    // 다음 실행이 다시 쓰는 폴더 (받은 도구·옛 exe·플러그인 zip·받은 파일 기억). 나머지는 실행마다 새로
    private static readonly string[] Reused = ["tools", "snapshots", "dl", "cache"];
    private static readonly string[] ToolDirs = ["python", "radare2", "opcodediff-venv"];

    public static string DefaultWorkDir(Region region, string version) =>
        Path.Combine(Path.GetTempPath(), "actrepack-replay", $"{region.Name}-{version}");

    public static ReplayResult Run(ReplayOptions o)
    {
        var sw = Stopwatch.StartNew();
        var region = o.Region;
        if (!Thaliak.Supports(region))
            throw new ToolException($"{region.Label} 은(는) 공식 패치 서버에서 옛 exe 를 받을 수 없어 재현할 수 없습니다.");
        if (!GameVersion.IsValid(o.Version))
            throw new ToolException($"게임 버전 형식이 아닙니다: {o.Version} (예: 2026.10.06.0000.0000)");

        string root = Path.GetFullPath(o.WorkDir ?? DefaultWorkDir(region, o.Version));
        OpenWorkDir(root);
        var ws = new Workspace(root);
        string userCache = HttpCache.Dir;
        HttpCache.Dir = Path.Combine(root, "cache", "http");
        try
        {
            using (ws.Lock())
            {
                CleanWorkDir(root);
                return RunIn(ws, o, sw);
            }
        }
        finally
        {
            HttpCache.Dir = userCache;
            if (!o.Keep && !Files.DeleteDirectoryQuietly(root)) Log.Note($"[replay] 작업 폴더를 지우지 못함: {root}");
        }
    }

    private static ReplayResult RunIn(Workspace ws, ReplayOptions o, Stopwatch sw)
    {
        var region = o.Region;
        ws.EnsureCreated();

        // 1) 출시: 준 버전이 미리 배포된 패치면 그 출시의 게임 버전으로 (출시 후 게임 버전 = 묶음의 가장 큰 버전)
        var releases = Thaliak.Releases(region);
        DateTime at = Thaliak.ReleasedAt(region, o.Version)
                      ?? throw new ToolException($"Thaliak 에 {region.Name} {o.Version} 버전의 출시 시각이 없습니다.");
        string version = releases.FirstOrDefault(r => Thaliak.ReleasedAt(region, r) == at) ?? o.Version;
        if (version != o.Version) Log.Note($"[replay] {o.Version} 은(는) {version} 출시에 묶인 패치 - {version} 으로 재현");
        string? previous = Thaliak.PreviousOf(releases, version);
        string? next = releases.FirstOrDefault(r => Thaliak.IsOlder(version, r));
        DateTime? nextAt = next != null ? Thaliak.ReleasedAt(region, next) : null;
        Log.Section($"[replay] {region.Name} {version}{Retail(version)}  출시 {at:yyyy-MM-dd HH:mm} UTC  직전 출시 {previous ?? "없음"}" +
                    (next != null ? $"  다음 출시 {next} ({nextAt:yyyy-MM-dd HH:mm} UTC)" : "  (최신 출시)"));

        // 2) 플러그인
        var pick = PickPlugins(GitHubRelease.List(PluginRelease.Repo), at, nextAt, o.BaseTag, o.RefTag);
        Log.Info($"[replay] 베이스 {pick.Base.Tag} ({Published(pick.Base)})  정답 " +
                 (pick.Reference == null ? "없음 (공식이 아직 나오지 않음 - 비교하지 않음)"
                  : pick.ReferenceIsBase ? $"{pick.Reference.Tag} (다음 출시까지 새 공식 릴리스가 없어 베이스 그대로)"
                  : $"{pick.Reference.Tag} ({Published(pick.Reference)})"));
        FetchPlugin(ws, pick.Base, ws.PluginDir);
        string? refDir = null;
        if (pick.Reference != null) FetchPlugin(ws, pick.Reference, refDir = Path.Combine(ws.Root, "ref"));

        // 3) 가짜 게임: 그 버전 exe + ffxivgame.ver
        if (o.ToolsDir != null) CopyTools(o.ToolsDir, ws.ToolsDir);
        string exe = Thaliak.SnapshotExe(ws, region, version);
        string gameDir = Path.Combine(ws.Root, "game", "game");
        Directory.CreateDirectory(gameDir);
        File.Copy(exe, Path.Combine(gameDir, "ffxiv_dx11.exe"), overwrite: true);
        File.WriteAllText(Path.Combine(gameDir, "ffxivgame.ver"), version);
        Config.Set(ws.Root, [KeyValuePair.Create("ida", (JsonNode?)JsonValue.Create(false)),
                             KeyValuePair.Create("localDiff", (JsonNode?)JsonValue.Create(true))]);
        Config.SetClients(ws.Root, new Dictionary<string, string> { [region.Name] = Path.Combine(ws.Root, "game") });
        var client = Config.Load(ws.Root).Clients.Single();

        // 4) prepare -> (패치가 필요하면) build
        Log.Info();
        Log.Section($"[replay] prepare (출처: {string.Join(", ", o.Sources)})");
        var prepared = Prepare.Run(ws, new PrepareOptions
        {
            Plugin = ws.PluginPath, UpdatePlugin = false, Csv = false, CheckLatest = false, Sources = [.. o.Sources], IdaFallback = false,
        });
        var steps = prepared.Steps.ToList();
        var checks = new List<ReplayCheck>();
        bool built = false;
        if (prepared.Outcome == PrepareOutcome.PatchNeeded)
        {
            Log.Info();
            Log.Section("[replay] build");
            try
            {
                Build.Run(ws, new BuildOptions { Csv = false, CsvOptional = true });
                built = true;
                steps.Add(new StepResult("build", StepStatus.Ok, "완료했습니다."));
            }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                Log.Warn($"[replay] build 실패 - {Errors.Describe(ex)}");
                steps.Add(new StepResult("build", StepStatus.Failed, Errors.Describe(ex)));
            }
            checks.Add(new ReplayCheck("build", built, built ? "패치본을 만들었습니다." : steps[^1].Message, []));
        }
        string ours = built ? ws.OutputPluginPath : ws.PluginPath;

        // 5) 정답과 비교
        if (refDir != null)
        {
            Log.Info();
            Log.Section($"[replay] 정답({pick.Reference!.Tag})과 비교 - 우리 결과: {(built ? @"output\" : @"베이스 그대로 (input\plugin)")}");
            checks.AddRange(Compare(ws, region, client, ours, Path.Combine(refDir, PluginRelease.PluginFile), prepared.Outcome));
        }
        return new ReplayResult(region, version, previous, at, pick.Base.Tag, pick.Reference?.Tag, pick.ReferenceIsBase, prepared.Outcome,
                                prepared.Verdict, steps, built, checks, sw.Elapsed, ws.Root, o.Keep);
    }

    private static List<ReplayCheck> Compare(Workspace ws, Region region, GameClient client, string ours, string reference, PrepareOutcome outcome)
    {
        var checks = new List<ReplayCheck>();

        // a) opcode (그 지역)
        var baseOps = ReadOpcodes(ws.PluginPath, region);
        var refOps = ReadOpcodes(reference, region);
        var ourOps = Files.SamePath(ours, ws.PluginPath) ? baseOps : ReadOpcodes(ours, region);
        int official = DiffOpcodes(baseOps, refOps).Count;
        var diffs = DiffOpcodes(ourOps, refOps);
        var details = diffs.Select(d => d.Describe()).ToList();
        // 만들지 않았으면(베이스 그대로) prepare 가 구해 둔 값도 알려 준다 — opcode 가 틀린 것인지, 다른 까닭으로 만들지 않은 것인지 가르게
        string prepared = Path.Combine(ws.OpcodesDir, region.Opcodes + ".txt");
        if (diffs.Count > 0 && Files.SamePath(ours, ws.PluginPath) && File.Exists(prepared))
        {
            int n = DiffOpcodes(MachinaOpcodes.Parse(File.ReadAllText(prepared), prepared), refOps).Count;
            details.Insert(0, $@"(패치본을 만들지 않아 베이스 값으로 비교함. prepare 가 구한 input\opcodes\{region.Opcodes}.txt 는 정답과 " +
                              (n == 0 ? "같습니다)" : $"{n}개 다릅니다)"));
        }
        checks.Add(new ReplayCheck($"opcode {region.Opcodes}", diffs.Count == 0,
            (diffs.Count == 0 ? $"정답과 같습니다 ({refOps.Count}개" : $"정답과 {diffs.Count}개 다릅니다 ({refOps.Count}개 중") +
            $", 공식이 베이스에서 바꾼 것 {official}개)", details));

        // 후크 시그니처 (읽지 못하면 — deucalion 배포본이 여러 개인 릴리스 등 — 그 까닭을 틀린 항목으로 남기고 계속)
        List<HookSig>? baseHooks = null, refHooks = null, ourHooks = null;
        string? hookError = null;
        try
        {
            baseHooks = HookSignatures(ws.PluginPath);
            refHooks = HookSignatures(reference);
            var inputs = SignatureInput.Load(Directory.Exists(ws.SignaturesDir) ? ws.SignaturesDir : null)
                .Select(s => new HookSig(s.Name, SignatureSources.Input, s.Pattern));
            ourHooks = inputs.Concat(HookSignatures(ours)).ToList();
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            hookError = $"후크 시그니처를 읽지 못했습니다: {Errors.Describe(ex)}";
            Log.Warn($"[replay] {hookError}");
        }

        // b) 판단: 정답의 opcode 나 후크 시그니처가 베이스와 다르면 "패치 필요"가 맞는 답
        var changedHooks = baseHooks != null && refHooks != null ? ChangedHooks(baseHooks, refHooks) : [];
        var why = new List<string>();
        if (official > 0) why.Add($"opcode {official}개");
        if (changedHooks.Count > 0) why.Add($"후크 시그니처 {string.Join("/", changedHooks)}");
        bool? expected = official > 0 || changedHooks.Count > 0 ? true : hookError != null ? null : false;
        checks.Add(expected is { } needed
            ? new ReplayCheck("판단", outcome == (needed ? PrepareOutcome.PatchNeeded : PrepareOutcome.NotNeeded),
                $"정답 {(needed ? $"패치 필요 (공식이 바꾼 것: {string.Join(", ", why)})" : "패치 불필요 (공식이 바꾼 것 없음)")}, prepare {OutcomeText(outcome)}", [])
            : new ReplayCheck("판단", false, $"정답을 정하지 못했습니다 (opcode 는 같고 후크 시그니처를 읽지 못함), prepare {OutcomeText(outcome)}", [hookError!]));

        // c) 후크: 새 exe 에서 정답과 같은 곳을 가리키는지 (후크마다 실제로 쓰일 시그니처 — input 교체 > deucalion 내장 > Machina 런타임)
        if (hookError != null) checks.Add(new ReplayCheck("후크", false, "비교하지 못했습니다.", [hookError]));
        var exe = SigLib.Target(client.ExePath);
        foreach (string group in hookError != null ? Enumerable.Empty<string>() : ourHooks!.Concat(refHooks!).Select(h => HookGroup(h.Name)).Distinct().Order(StringComparer.Ordinal))
        {
            var (ourPoints, ourVia) = EffectivePoints(ourHooks!, group, exe);
            var (refPoints, refVia) = EffectivePoints(refHooks!, group, exe);
            bool same = ourPoints.SetEquals(refPoints);
            string summary = same
                ? ourPoints.Count == 0 ? "둘 다 맞는 곳 없음" : $"정답과 같은 {ourPoints.Count}곳 (우리 {ourVia}, 정답 {refVia})"
                : "정답과 다른 곳을 가리킵니다";
            checks.Add(new ReplayCheck($"후크 {group}", same, summary,
                same ? [] : [$"우리 {DescribePoints(ourPoints)} ({ourVia ?? "맞는 것 없음"})", $"정답 {DescribePoints(refPoints)} ({refVia ?? "맞는 것 없음"})"]));
        }

        // d) sigcheck: 우리 결과로 후크가 다 맞는지
        Log.Info();
        Log.Section("[replay] 우리 결과로 sigcheck");
        try
        {
            var sc = Sigcheck.Execute(ours, ws.SignaturesDir, [client]);
            checks.Add(new ReplayCheck("sigcheck", sc.CriticalOk, sc.CriticalOk ? "후크가 모두 맞습니다." : "깨진 후크가 있습니다.",
                sc.Broken.Where(b => b.Critical).Select(b => b.Group).Distinct().ToList()));
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            checks.Add(new ReplayCheck("sigcheck", false, "점검이 실패했습니다.", [Errors.Describe(ex)]));
        }
        return checks;
    }

    // ---- 플러그인 고르기 ----

    // Reference: 정답 (null: 정답 없음), ReferenceIsBase: 다음 출시까지 새 공식 릴리스가 없어 베이스를 정답으로 봄
    internal sealed record PluginPick(GitHubRelease Base, GitHubRelease? Reference, bool ReferenceIsBase);

    // releases: 공식 릴리스 목록 (게시 시각), releasedAt: 출시 시각, nextReleasedAt: 다음 출시 시각 (없으면 null — 최신 출시).
    // 시험판·게시 시각이 없는 것은 자동으로 고르지 않는다. 태그를 주면 그것으로.
    internal static PluginPick PickPlugins(IReadOnlyList<GitHubRelease> releases, DateTime releasedAt, DateTime? nextReleasedAt,
                                           string? baseTag = null, string? refTag = null)
    {
        var published = releases.Where(r => r.PublishedAt != null && !r.Prerelease).OrderBy(r => r.PublishedAt).ToList();
        GitHubRelease Find(string tag) => releases.FirstOrDefault(r => r.Tag == tag)
            ?? throw new ToolException($"공식 릴리스 목록(최근 {releases.Count}개)에 {tag} 이(가) 없습니다.");
        var @base = baseTag != null ? Find(baseTag)
            : published.LastOrDefault(r => r.PublishedAt <= releasedAt)
              ?? throw new ToolException($"출시({releasedAt:yyyy-MM-dd HH:mm} UTC) 전에 게시된 공식 릴리스가 목록(최근 {releases.Count}개)에 없습니다. --base 로 주세요.");
        if (refTag != null) return new PluginPick(@base, Find(refTag), refTag == @base.Tag);
        var after = published.FirstOrDefault(r => r.PublishedAt > releasedAt);
        if (after != null && (nextReleasedAt == null || after.PublishedAt < nextReleasedAt)) return new PluginPick(@base, after, false);
        // 다음 출시까지 새 릴리스가 없었으면 베이스가 이 출시에도 맞았던 것. 다음 출시도 없으면 아직 모른다
        return nextReleasedAt != null ? new PluginPick(@base, @base, true) : new PluginPick(@base, null, false);
    }

    // ---- opcode ----

    internal sealed record OpcodeDiff(string Name, int? Ours, int? Reference)
    {
        public string Describe() => $"{Name}: 우리 {Hex(Ours)} / 정답 {Hex(Reference)}";
        private static string Hex(int? v) => v is { } x ? $"0x{x:X}" : "없음";
    }

    // 값이 다른 이름과 한쪽에만 있는 이름 (이름순)
    internal static List<OpcodeDiff> DiffOpcodes(IReadOnlyDictionary<string, int> ours, IReadOnlyDictionary<string, int> reference) =>
        ours.Keys.Union(reference.Keys).Order(StringComparer.Ordinal)
            .Select(k => new OpcodeDiff(k, ours.TryGetValue(k, out int a) ? a : null, reference.TryGetValue(k, out int b) ? b : null))
            .Where(d => d.Ours != d.Reference)
            .ToList();

    private static Dictionary<string, int> ReadOpcodes(string pluginPath, Region region)
    {
        using var plugin = Repacker.ReadPlugin(pluginPath);
        return MachinaOpcodes.Parse(MachinaOpcodes.ReadBase(plugin, region.Opcodes), $"{pluginPath} {region.Opcodes}");
    }

    // ---- 후크 ----

    // Source: 표에 보일 출처 (SignatureSources)
    internal sealed record HookSig(string Name, string Source, string Pattern);

    // 플러그인(과 같은 폴더의 deucalion)의 패킷 후크 시그니처: deucalion 내장, 다음에 Machina 런타임 RECV (3.0.3.3+ —
    // 내장 Recv 가 맞지 않을 때만 Machina 가 보낸다)
    internal static List<HookSig> HookSignatures(string pluginPath)
    {
        var list = new List<HookSig>();
        if (Deucalion.FindDistrib(Path.GetDirectoryName(pluginPath)!) is { } distrib)
            list.AddRange(Deucalion.ReadSignatures(File.ReadAllBytes(distrib)).Select(s => new HookSig(s.Name, SignatureSources.Deucalion, s.Pattern)));
        using var plugin = Repacker.ReadPlugin(pluginPath);
        using var machina = Costura.ReadInner(plugin, Costura.Machina);
        list.AddRange(DeucalionMachina.RuntimeSignatures(machina).Select(i => new HookSig("Recv", SignatureSources.Machina, (string)i.Operand)));
        return list;
    }

    // 후크 묶음 이름 (Sigcheck 와 같이: CreateTarget.Normal -> CreateTarget)
    internal static string HookGroup(string name) => name.Split('.')[0];

    // 시그니처(이름·출처·패턴)가 정답에서 바뀐 후크 묶음 (띄어쓰기만 다른 것은 같은 것)
    internal static List<string> ChangedHooks(IReadOnlyList<HookSig> before, IReadOnlyList<HookSig> after)
    {
        static HashSet<(string, string, string)> Keys(IEnumerable<HookSig> s) => s.Select(h => (h.Name, h.Source, h.Pattern.Replace(" ", ""))).ToHashSet();
        return before.Concat(after).Select(h => HookGroup(h.Name)).Distinct().Order(StringComparer.Ordinal)
            .Where(g => !Keys(before.Where(h => HookGroup(h.Name) == g)).SetEquals(Keys(after.Where(h => HookGroup(h.Name) == g))))
            .ToList();
    }

    // 묶음 안에서 실제로 쓰일 시그니처가 가리키는 곳: 적힌 차례(input > deucalion > Machina)로, CreateTarget 은 Normal > Alt > Fallback
    // 차례로 시도해 처음 맞는 것. 맞는 것이 없으면 빈 집합. Via: 쓴 것 ("deucalion Recv")
    private static (HashSet<int> Points, string? Via) EffectivePoints(IReadOnlyList<HookSig> sigs, string group, ScanTarget exe)
    {
        foreach (var h in sigs.Where(h => HookGroup(h.Name) == group).Select((h, i) => (h, i))
                     .OrderBy(x => SourceRank(x.h.Source)).ThenBy(x => VariantRank(x.h.Name)).ThenBy(x => x.i).Select(x => x.h))
        {
            var matches = SigLib.Parse(h.Pattern).Scan(exe);
            if (Sigcheck.Usable(SignatureKinds.Deucalion, h.Name, matches.Count))
                return (matches.Select(m => Sigcheck.PointOf(m, SignatureKinds.Deucalion)).ToHashSet(), $"{h.Source} {h.Name}");
        }
        return ([], null);

        static int SourceRank(string s) => s == SignatureSources.Input ? 0 : s == SignatureSources.Deucalion ? 1 : 2;
        static int VariantRank(string name) => name.Split('.') is [_, var v] ? v switch { "Normal" => 0, "Alt" => 1, "Fallback" => 2, _ => 3 } : 0;
    }

    private static string DescribePoints(HashSet<int> points) =>
        points.Count == 0 ? "0곳" : $"{points.Count}곳 [{string.Join(", ", points.Order().Take(4).Select(p => $"0x{p:X}"))}{(points.Count > 4 ? ", ..." : "")}]";

    // ---- 작업 폴더 ----

    // 작업 폴더를 연다: 없으면 만들고, 이미 있는데 replay 가 만든 폴더(표시 파일)가 아니면 멈춘다 (엉뚱한 폴더를 지우지 않게)
    private static void OpenWorkDir(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(Path.Combine(root, Marker)))
            throw new ToolException($"replay 작업 폴더가 아닌 폴더입니다 (비어 있지 않음): {root}. 빈 폴더나 새 경로를 주세요.");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Marker), "ActPluginRepack replay 작업 폴더 - 지워도 됩니다.");
    }

    // 지난 실행이 남긴 재료·결과를 지운다 (다시 쓰는 폴더와 잠금·표시 파일은 남김)
    private static void CleanWorkDir(string root)
    {
        foreach (string d in Directory.GetDirectories(root).Where(d => !Reused.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
            if (!Files.DeleteDirectoryQuietly(d)) Directory.Delete(d, recursive: true); // 못 지우면 그 까닭을 예외로
        foreach (string f in Directory.GetFiles(root).Where(f => Path.GetFileName(f) is not (Marker or ".actrepack.lock")))
            File.Delete(f);
    }

    // 공식 릴리스 zip 을 받아(dl\ 에 남겨 다음에 다시 씀) dir 에 푼다
    private static void FetchPlugin(Workspace ws, GitHubRelease release, string dir)
    {
        string url = PluginRelease.ZipUrl(release) ?? throw new ToolException($"{release.Tag} 릴리스에 플러그인 zip 이 없습니다.");
        string saved = Path.Combine(ws.Root, "dl", Path.GetFileName(new Uri(url).LocalPath));
        byte[] zip;
        if (File.Exists(saved)) zip = File.ReadAllBytes(saved);
        else
        {
            Log.Note($"[replay] {release.Tag} 받는 중: {url}");
            zip = Http.GetBytes(url);
            Files.WriteAtomic(saved, zip);
        }
        try
        {
            Files.FillDirectory(dir, _ => PluginRelease.ExtractZip(zip, dir, url));
        }
        catch (InvalidDataException ex)
        {
            Files.TryDeleteFile(saved); // 깨진 zip 은 다음에 새로 받게
            throw new ToolException($"{release.Tag} 릴리스 zip 을 풀지 못했습니다: {ex.Message}");
        }
    }

    // 받아 둔 도구(python / radare2 / opcodediff-venv)를 작업 폴더 tools\ 로 복사 — 새로 받고 설치하는 시간을 아낀다.
    // 이미 있는 것은 그대로 (지난 실행이 복사했거나 받아 둔 것)
    private static void CopyTools(string from, string to)
    {
        from = Path.GetFullPath(from);
        if (!Directory.Exists(from)) throw new ToolException($"도구 폴더가 없습니다: {from}");
        if (Files.SamePath(from, to)) return;
        foreach (string name in ToolDirs)
        {
            string src = Path.Combine(from, name), dst = Path.Combine(to, name);
            if (!Directory.Exists(src) || Directory.Exists(dst)) continue;
            Log.Note($@"[replay] tools\{name} <- {src}");
            Files.InstallDirectory(dst, tmp => CopyTree(src, tmp));
        }
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(src))
            if (!new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ---- 문구 ----

    // 결과 표 (CLI 와 선택 실행 시험이 같은 모양으로): 단계 요약, 항목별 OK/!! 와 다른 것, 마지막에 맞음/틀림과 걸린 시간
    public static void Report(ReplayResult r)
    {
        Log.Info();
        Log.Section($"[replay] 단계 요약 - prepare {OutcomeText(r.Outcome)}: {r.Verdict}");
        foreach (var step in r.Steps) Log.Write(step.SummaryKind, step.SummaryLine());
        Log.Info();
        Log.Section($"[replay] {r.Region.Name} {r.Version}{Retail(r.Version)}  베이스 {r.BaseTag}  정답 " +
                    (r.Reference == null ? "없음" : r.ReferenceIsBase ? $"{r.Reference} (베이스 그대로)" : r.Reference));
        foreach (var c in r.Checks)
        {
            Log.Write(c.Pass ? LogKind.Done : LogKind.Warn, c.Line());
            foreach (string d in c.Details) Log.Write(c.Pass ? LogKind.Info : LogKind.Warn, $"         - {d}");
        }
        string elapsed = $"걸린 시간 {(int)r.Elapsed.TotalMinutes}:{r.Elapsed.Seconds:00}";
        string kept = r.Kept ? $", 작업 폴더 {r.WorkDir}" : "";
        if (!r.Compared) Log.Note($"[replay] 정답(공식 릴리스)이 아직 없어 비교하지 않았습니다 ({elapsed}{kept})");
        else if (r.Pass) Log.Done($"[replay] 모두 정답과 같습니다 ({elapsed}{kept})");
        else Log.Error($"[replay] 정답과 다른 것이 {r.Checks.Count(c => !c.Pass)}개 있습니다 ({elapsed}{kept})");
    }

    public static string OutcomeText(PrepareOutcome outcome) => outcome switch
    {
        PrepareOutcome.PatchNeeded => "패치 필요",
        PrepareOutcome.NotNeeded => "패치 불필요",
        PrepareOutcome.Undecided => "판단하지 못함",
        PrepareOutcome.HooksBroken => "후크가 깨진 채 끝남",
        PrepareOutcome.OpcodesMissing => "opcode 를 구하지 못함",
        _ => outcome.ToString(),
    };

    // " (7.57)" — 패치 번호를 모르면 빈 문자열
    public static string Retail(string version)
    {
        try { return GameVersions.RetailOf(version) is { } r ? $" ({r})" : ""; }
        catch (Exception ex) when (Errors.Recoverable(ex)) { return ""; }
    }

    private static string Published(GitHubRelease r) => r.PublishedAt is { } t ? $"{t:yyyy-MM-dd HH:mm} UTC 게시" : "게시 시각 모름";
}
