using Mono.Cecil;

// 패치된 게임 클라이언트에 현재 플러그인/deucalion 의 시그니처가 아직 맞는지 점검한다.
//
// 종류(Kind)와 출처(Source):
//   deucalion  패킷 후킹 — deucalion dll 내장(Recv / Send / SendLobby / CreateTarget.*),
//              machina(Machina 가 런타임에 보내는 RECV, 3.0.3.3+), input(input\signatures\deucalion.txt)
//              깨지면 패킷이 안 들어오므로 "치명" — 종료 코드 2.
//   memory     플러그인 메모리 읽기 7종 — plugin(내장 리소스), input(memory.txt). 깨지면 경고만.
//   oodle      Machina Oodle 함수 12종 — plugin(Machina IL), input(oodle.txt). 깨지면 경고만
//              (deucalion 캡처에는 안 쓰임. *_Train 은 공식 플러그인에서도 이미 0건).
// input 에 같은 항목이 있으면 그게 쓰일 값이므로 그것으로 판정한다.
//
// 클라이언트는 config.json 의 clients, 또는 --game a,b 로 지정한다.
// 종료 코드: 0 = 치명 항목 정상, 2 = 깨진 deucalion 후크 있음.
internal static class Sigcheck
{
    // Variant: deucalion 은 "*", memory 는 Global/TraditionalChinese, oodle 은 Global/Korean
    public sealed record Item(string Kind, string Variant, string Name)
    {
        public string Group => Kind == SignatureKinds.Deucalion ? Name.Split('.')[0] : $"{Kind} {Variant}.{Name}";
        public bool Critical => Kind == SignatureKinds.Deucalion;
    }

    public sealed record Row(Item Item, string Source, SigPattern Pattern);

    // 깨진 항목: 그 클라이언트에서 쓰일 원래(입력 아닌) 패턴들이 전부 0건 (deucalion 후크는 정해진 개수가 아니면 0건과 같다)
    public sealed record Broken(GameClient Client, string Group, bool Critical, IReadOnlyList<Row> Original);

    // CriticalOk: 실제로 쓰일 시그니처(input 우선)로 후크가 다 맞음.
    // OfficialBroken: 공식 플러그인/deucalion 원래 시그니처만으로는 깨진 항목 (input 없이) — 패치가 필요한 이유.
    // ObsoleteInputs: input 에 있지만 원래 시그니처가 모든 클라이언트에서 이미 맞는 항목 — 공식이 따라잡아 필요 없어진 것.
    public sealed record Result(bool CriticalOk, List<Broken> Broken, IReadOnlyList<Item> OfficialBroken, IReadOnlyList<Item> ObsoleteInputs)
    {
        public bool OfficialHooksOk => OfficialBroken.All(i => !i.Critical);

        // 공식 메모리 시그니처는 깨졌지만 교체 시그니처로 맞는 항목이 있음 (패치하면 그 기능이 살아남)
        public bool MemoryFixedByInput => OfficialBroken.Any(i => i.Kind == SignatureKinds.Memory && Broken.All(b => b.Group != i.Group));

        // 클라이언트마다 실제로 쓰일 시그니처 (그룹마다 쓰일 수 있는 첫 행 — 깨진 그룹은 없음). 조용한 어긋남 검사(SignatureDrift)가 쓴다.
        public IReadOnlyList<(GameClient Client, Row Row)> InUse { get; init; } = [];
    }

    public static Result Execute(string pluginPath, string sigDir, List<GameClient> clients)
    {
        var rows = CollectRows(pluginPath, sigDir);
        var broken = new List<Broken>();
        var officialBroken = new HashSet<Item>();
        var inUse = new List<(GameClient, Row)>();
        foreach (var client in clients)
        {
            Operation.ThrowIfCancelled();
            Log.Info();
            Log.Section($"[{client.Name}] {client.Version ?? "(버전 모름)"}  {client.ExePath}");
            var (b, ob, used) = Check(client, rows.Where(r => Applies(r.Item, client.Region)).ToList());
            broken.AddRange(b);
            officialBroken.UnionWith(ob);
            inUse.AddRange(used.Select(r => (client, r)));
        }
        // 그룹 단위: 원래 시그니처 중 하나라도 맞으면 그 그룹은 공식본으로 됨 (CreateTarget 후보, Recv 내장/런타임)
        var officialBrokenGroups = officialBroken.Select(i => i.Group).ToHashSet();
        // 이번에 실제로 점검한 변형만 (점검하지 않은 변형 — 그 변형을 쓰는 클라이언트가 없거나 제외됨 — 은 따라잡았는지 모른다)
        var obsolete = rows.Where(r => r.Source == SignatureSources.Input && clients.Any(c => Applies(r.Item, c.Region)) &&
                                       !officialBrokenGroups.Contains(r.Item.Group))
            .Select(r => r.Item).Distinct().ToList();
        Log.Info();
        bool criticalOk = broken.All(b => !b.Critical);
        int warn = broken.Count(b => !b.Critical);
        if (criticalOk && warn == 0) Log.Done("[sigcheck] 모두 정상");
        else if (criticalOk) Log.Warn($"[sigcheck] 후크 정상, 메모리/Oodle 시그니처 {warn}개 깨짐 (경고)");
        else Log.Error($@"[sigcheck] 깨진 후크 있음 - input\signatures\{SignatureInput.FileName} 에 '이름|패턴' 줄을 넣을 것");
        return new Result(criticalOk, broken, officialBroken.ToList(), obsolete) { InUse = inUse };
    }

    // 일치 하나가 가리키는 곳 (자동 복구의 후보 검증, replay 의 후크 비교가 같은 규칙으로): call 형은 따라간 대상
    // (deucalion 은 가장 깊은 북마크를 쓴다), memory/oodle 은 패턴 끝(플러그인이 그 바로 뒤를 읽음), deucalion 의 나머지는
    // 시작 위치(거기를 후킹)
    public static int PointOf(SigPattern.Match m, string kind) =>
        m.Saves.Count > 0 ? m.Saves[^1] : kind == SignatureKinds.Deucalion ? m.Rva : m.End;

    // 이 일치 수로 그 시그니처가 쓰일 수 있는지 (점검·자동 복구·지난 패치 재현이 같은 규칙으로): deucalion 후크는 정해진 개수와
    // 같아야 하고(맞는 곳이 있어도 개수가 다르면 deucalion 이 후크를 걸지 않음), 나머지는 1곳 이상
    public static bool Usable(string kind, string name, int matches) =>
        kind == SignatureKinds.Deucalion ? Deucalion.Usable(name, matches) : matches > 0;

    // 일치가 실제로 가리키는 것: memory/oodle 은 패턴 바로 뒤에서 읽는 값(RIP 상대 — 플러그인·Machina 가 쓰는 것. 읽지 못하면
    // 패턴 끝), 그 밖은 PointOf. 여러 곳에 맞는 메모리 시그니처(ZoneID, ServerTime)는 모두 같은 값을 읽으므로 하나로 센다 —
    // 패턴 끝 위치로 세면 한 곳에만 맞는 옳은 교체 시그니처가 "가리키는 곳 수가 다름"으로 탈락한다.
    public static int TargetOf(SigPattern.Match m, ScanTarget exe, string kind) =>
        kind is SignatureKinds.Memory or SignatureKinds.Oodle && SigValue.Resolve(exe.Image, m, 0, rip: true).ResolvedRva is { } value
            ? value : PointOf(m, kind);

    // 패턴이 exe 에서 가리키는 것들 (TargetOf)
    public static HashSet<int> Points(SigPattern sig, ScanTarget exe, string kind) =>
        sig.Scan(exe).Select(m => TargetOf(m, exe, kind)).ToHashSet();

    private static bool Applies(Item item, Region? region) => Applies(item.Kind, item.Variant, region);

    // 이 지역 클라이언트가 그 시그니처를 쓰는지: memory/oodle 은 지역이 쓰는 변형만, deucalion 은 모두
    // (build 의 교체 시그니처 검증, 자동 복구의 후보 검증도 이것으로 고른다)
    public static bool Applies(string kind, string variant, Region? region) => kind switch
    {
        SignatureKinds.Memory => variant == PluginSignatures.MemoryVariantFor(region),
        SignatureKinds.Oodle => variant == PluginSignatures.OodleVariantFor(region),
        _ => true,
    };

    private static List<Row> CollectRows(string pluginPath, string sigDir)
    {
        var rows = new List<Row>();
        string? distrib = Deucalion.FindDistrib(Path.GetDirectoryName(pluginPath)!);
        byte[]? deucalion = distrib != null ? File.ReadAllBytes(distrib) : null;
        if (deucalion != null)
            rows.AddRange(Deucalion.ReadSignatures(deucalion)
                .Select(s => new Row(new Item(SignatureKinds.Deucalion, "*", s.Name), SignatureSources.Deucalion, SigLib.Parse(s.Pattern))));
        else Log.Note(@"[deucalion] input\plugin 에 deucalion*.distrib.dll 없음 - deucalion 시그니처 생략");

        if (File.Exists(pluginPath))
        {
            using var plugin = Repacker.ReadPlugin(pluginPath);
            using (var machina = Costura.ReadInner(plugin, Costura.Machina))
            {
                string? expected = DeucalionMachina.ReadChecksum(machina);
                if (deucalion != null)
                {
                    bool match = DeucalionMachina.Matches(expected, deucalion);
                    Log.Info($"[deucalion] {Path.GetFileName(distrib)}  " +
                                      (match ? "checksum = Machina _checksum" : $"checksum MISMATCH (Machina expects {expected})"));
                }
                var runtime = DeucalionMachina.RuntimeSignatures(machina);
                Log.Info(runtime.Count > 0
                    ? "[machina]   runtime RECV signature: yes (Recv 교체는 Machina 문자열 + deucalion 내장 둘 다)"
                    : "[machina]   runtime RECV signature: no (구버전 - Recv 교체도 deucalion dll 을 직접 패치)");
                rows.AddRange(runtime.Select(i => new Row(new Item(SignatureKinds.Deucalion, "*", "Recv"), SignatureSources.Machina, SigLib.Parse((string)i.Operand))));
            }
            rows.AddRange(PluginSignatures.Read(plugin)
                .Select(s => new Row(new Item(s.Kind, s.Variant, s.Name), SignatureSources.Plugin, SigLib.Parse(s.Pattern))));
        }
        else Log.Note($"[machina]   플러그인 없음: {pluginPath}");

        string? dir = Directory.Exists(sigDir) ? sigDir : null;
        rows.AddRange(SignatureInput.Load(dir)
            .Select(s => new Row(new Item(SignatureKinds.Deucalion, "*", s.Name), SignatureSources.Input, SigLib.Parse(s.Pattern))));
        foreach (string kind in new[] { SignatureKinds.Memory, SignatureKinds.Oodle })
        {
            var variants = rows.Where(r => r.Item.Kind == kind).Select(r => r.Item.Variant).Distinct().ToList();
            foreach (var (variant, name, pattern) in SignatureInput.Resolve(SignatureInput.Load(dir, SignatureInput.FileFor(kind)), variants))
                rows.Add(new Row(new Item(kind, variant, name), SignatureSources.Input, SigLib.Parse(pattern)));
        }
        if (rows.Count == 0) throw new ToolException("점검할 시그니처가 없습니다.");
        return rows;
    }

    // 표를 출력하고 항목(그룹)별로 실제 쓰일 시그니처가 맞는지 판정한다.
    //   input 에 있으면 그게 쓰인다. 없으면 원래 것 (Recv 는 deucalion 내장과 machina 런타임 중 하나면 됨).
    //   CreateTarget.* 은 deucalion 이 차례로 시도하는 후보라 하나만 맞으면 된다.
    private static (List<Broken> Broken, List<Item> OfficialBroken, List<Row> InUse) Check(GameClient client, List<Row> rows)
    {
        var exe = SigLib.Target(client.ExePath);
        var hits = rows.ToDictionary(r => r, r => r.Pattern.Scan(exe));
        int width = rows.Max(r => r.Item.Kind == SignatureKinds.Deucalion ? r.Item.Name.Length : r.Item.Name.Length + r.Item.Kind.Length + 1);
        string Label(Row r) => r.Item.Kind == SignatureKinds.Deucalion ? r.Item.Name : $"{r.Item.Kind}.{r.Item.Name}";
        bool Fits(Row r) => Usable(r.Item.Kind, r.Item.Name, hits[r].Count);

        foreach (var r in rows.OrderBy(r => r.Item.Kind == SignatureKinds.Deucalion ? 0 : r.Item.Kind == SignatureKinds.Memory ? 1 : 2))
        {
            var m = hits[r];
            int targets = m.Where(x => x.Saves.Count > 0).Select(x => x.Saves[0]).Distinct().Count();
            string count = m.Count == 0 ? "0" : targets > 0 ? $"{m.Count} (대상 {targets})" : $"{m.Count}";
            string line = $"  {r.Source,-9} {Label(r).PadRight(width)}  {count,-14} {r.Pattern.Text}";
            // 행 단위 0건은 경고만 (input 으로 대체됐거나 CreateTarget 후보일 수 있음). 판정은 아래 => 줄.
            if (m.Count == 0) Log.Warn(line + "   <- 맞는 곳 없음");
            else if (!Fits(r)) Log.Warn(line + $"   <- deucalion 은 정확히 {Deucalion.RequiredMatches(r.Item.Name)}곳이어야 씀");
            else Log.Info(line);
        }

        var broken = new List<Broken>();
        var officialBroken = new List<Item>();
        var inUse = new List<Row>();
        foreach (var group in rows.GroupBy(r => r.Item.Group).OrderBy(g => g.First().Item.Critical ? 0 : 1))
        {
            bool critical = group.First().Item.Critical;
            if (group.Any(r => r.Source != SignatureSources.Input) && !group.Any(r => r.Source != SignatureSources.Input && Fits(r)))
                officialBroken.Add(group.First(r => r.Source != SignatureSources.Input).Item);
            Row? via = null;
            foreach (var item in group.Select(r => r.Item).Distinct())
            {
                var input = group.FirstOrDefault(r => r.Item == item && r.Source == SignatureSources.Input);
                IEnumerable<Row> used = input != null ? [input] : group.Where(r => r.Item == item && r.Source != SignatureSources.Input);
                via = used.FirstOrDefault(Fits);
                if (via != null) break;
            }
            if (via != null)
            {
                if (critical || via.Source == SignatureSources.Input) Log.Info($"  => {group.Key}: 정상 ({via.Source} {via.Item.Name})");
                inUse.Add(via);
                continue;
            }
            var original = group.Where(r => r.Source != SignatureSources.Input).ToList();
            broken.Add(new Broken(client, group.Key, critical, original));
            if (critical) Log.Error($"  => {group.Key}: 깨짐");
            else Log.Warn($"  => {group.Key}: 깨짐 (경고)");
        }
        return (broken, officialBroken, inUse);
    }
}
