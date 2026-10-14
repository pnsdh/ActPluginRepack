// 한 번에 임시 플러그인 만들기: input\ 의 재료로 output\FFXIV_ACT_Plugin.dll 생성.
//   1) input\csv\<lang> 의 데이터마이닝 CSV -> data\*.txt (언어별 자동 변환)
//   2) input\signatures 의 교체 시그니처를 config.json 의 게임 클라이언트로 검증
//   3) input\opcodes + input\overrides + data + 시그니처를 원본 플러그인에 주입 -> output\
//      (deucalion 을 패치해야 하면 패치본도 output\ 에)
//   4) input\plugin 의 부속 파일(베이스 dll 제외)을 output\ 에 복사
//      -> output 폴더를 ACT 플러그인 폴더에 통째로 복사하면 되게.
//
// data\ 는 항상 CSV에서 생성되는 산출물이므로 손으로 건드리지 않는다
// (직접 입력은 input\ 의 csv/opcodes/overrides 로만).
internal sealed record BuildOptions
{
    public string? Plugin { get; init; }   // 베이스 플러그인 직접 지정 (기본 input\plugin)
    public bool Csv { get; init; } = true; // false: data\ 를 다시 만들지 않고 그대로 씀
}

// Warnings: 빌드는 했지만 알아야 할 것 (오래된 재료 등)
internal sealed record BuildResult(string OutputDir, IReadOnlyList<string> Files, IReadOnlyList<string> Warnings, BuildInfo Info);

// output\.build.json — 무엇으로 만든 결과인지 (설치/상태 표시용)
internal sealed record BuildInfo(DateTime BuiltAt, string? PluginVersion, Dictionary<string, string> GameVersions, bool? PatchNeeded)
{
    public const string FileName = ".build.json";
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BuildInfo? Load(string outputDir)
    {
        string path = Path.Combine(outputDir, FileName);
        if (!File.Exists(path)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(path), Json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public void Save(string dir) => File.WriteAllText(Path.Combine(dir, FileName), System.Text.Json.JsonSerializer.Serialize(this, Json));
}

internal static class Build
{
    public static BuildResult Run(Workspace ws, BuildOptions o)
    {
        string dataDir = ws.DataDir;
        string pluginPath = Path.GetFullPath(o.Plugin ?? ws.PluginPath);
        var config = Config.Load(ws.Root);
        var warnings = new List<string>(config.ClientErrors.Select(e => $"{Config.FileName}: {e} 이 클라이언트는 시그니처 검증에서 뺍니다."));

        // 0) 재료가 지금 게임/플러그인 것인지 — prepare 기록과 비교. 최신 버전이 아니라 prepare 가 뺀 클라이언트는
        //    (그 뒤로 그대로면) 시그니처 검증에서도 뺀다 — 옛 exe 에는 새 시그니처가 맞지 않는다.
        var state = MaterialState.Load(ws);
        var clients = config.Clients.Where(c => state?.IsOutdated(c) != true).ToList();
        foreach (var c in config.Clients.Except(clients))
            Log.Note($"[build] {c.Name}: 게임이 최신 버전이 아니라 prepare 에서 뺀 클라이언트 - 시그니처 검증 생략");
        if (state != null)
        {
            foreach (string d in state.Differences(pluginPath, config.Clients))
                warnings.Add($"{d} prepare 를 다시 실행하세요.");
            if (state.PatchNeeded == false)
                warnings.Add("마지막 prepare 의 판단으로는 공식 플러그인이 이미 맞아 임시 패치가 필요 없습니다.");
            // opcode 를 못 구한 클라이언트는 input\opcodes 에 예전 값이 남아 있을 수 있다
            foreach (var c in clients.Where(c => c.Region != null && !state.Opcodes.ContainsKey(c.Region.Name)))
                warnings.Add($"{c.Name}: 마지막 prepare 에서 opcode 를 구하지 못했습니다. 이대로 만들면 예전 값이 들어갈 수 있습니다.");
        }
        else warnings.Add("prepare 기록이 없어 input\\ 이 지금 게임 버전 것인지 확인할 수 없습니다. 직접 준비했다면 무시하세요.");
        foreach (string w in warnings) Log.Warn($"[build] {w}");
        Operation.ThrowIfCancelled();

        // 1) CSV -> data
        //   - 다국어 리스트(Action/Status/Territory/ContentFinder): 모든 언어 폴더마다 생성.
        //   - 고정 3종(ActionCategory/World/Map, 플러그인이 _English 만 읽음): input\csv\en 이
        //     있을 때만 en 에서 생성(영어->영어). en 이 없으면 베이스 DLL 영어값을 그대로 둔다.
        if (o.Csv)
        {
            string csvRoot = ws.CsvDir;
            var known = Region.All.SelectMany(r => r.CsvLeafs).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var langDirs = Directory.Exists(csvRoot)
                ? Directory.GetDirectories(csvRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            foreach (string d in langDirs.Where(d => !known.Contains(Path.GetFileName(d))))
                Log.Warn($@"[csv] input\csv\{Path.GetFileName(d)} - 알 수 없는 언어 폴더라 건너뜀 ({string.Join("/", known)} 만 씀)");
            langDirs = langDirs.Where(d => known.Contains(Path.GetFileName(d))).ToList();
            if (langDirs.Count == 0)
            {
                Log.Note(@"[csv] input\csv 에 언어 폴더 없음 - 데이터 생성 건너뜀");
            }
            else
            {
                // data 를 매번 CSV 에서 새로 생성(오래된 파일 잔류 방지). 변환 실패는 곧바로
                // 빌드 실패다 — 계속 진행하면 누락분이 조용히 베이스 DLL 값으로 남기 때문.
                // 임시 폴더에 다 만든 뒤 바꿔 넣어, 실패해도 이전 data\ 가 반쪽이 되지 않게 한다.
                string dataTmp = dataDir + ".tmp";
                if (Directory.Exists(dataTmp)) Directory.Delete(dataTmp, recursive: true);
                foreach (string dir in langDirs)
                {
                    string leaf = Path.GetFileName(dir);
                    bool pinned = leaf.Equals("en", StringComparison.OrdinalIgnoreCase);
                    string[] lists = pinned ? [.. CsvConverter.DefaultLists, .. CsvConverter.PinnedLists]
                        : CsvConverter.IsChineseLeaf(leaf) ? CsvConverter.ChineseLists
                        : CsvConverter.DefaultLists;
                    Log.Section($"[csv] {leaf} ->{(pinned ? " (+고정 3종)" : "")}");
                    CsvConverter.Convert(dir, dataTmp, lang: null, lists);
                }
                if (Directory.Exists(dataDir))
                    foreach (string f in Directory.GetFiles(dataDir, "*.txt")) File.Delete(f);
                Directory.CreateDirectory(dataDir);
                foreach (string f in Directory.GetFiles(dataTmp))
                    File.Move(f, Path.Combine(dataDir, Path.GetFileName(f)), overwrite: true);
                Directory.Delete(dataTmp, recursive: true);
                if (!langDirs.Any(d => Path.GetFileName(d).Equals("en", StringComparison.OrdinalIgnoreCase)))
                    Log.Note("[csv] en 폴더 없음 - 고정 3종(ActionCategory/World/Map)은 베이스 DLL 유지");
            }
        }

        // 2) 교체할 시그니처가 있으면 config.json 의 클라이언트에서 실제로 맞는지 먼저 확인
        string? sigDir = ExistingOrNull(ws.SignaturesDir);
        VerifySignatures(clients, sigDir);

        // 3) repack -> output (폴더가 없는 단계는 자동 생략). 임시 폴더에 다 만든 뒤 output\ 의 DLL 을 통째로 바꾼다
        //    — 실패하면 이전 output\ 그대로, 성공하면 이전 빌드의 DLL(옛 deucalion 등)이 남지 않음.
        Log.Section("[repack]");
        string outDir = ws.OutputDir;
        string stage = outDir + ".tmp";
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        string pluginDir = Path.GetDirectoryName(pluginPath)!;
        CheckDeucalionPair(pluginPath);
        var written = Repacker.Execute(
            pluginPath: pluginPath,
            outputPath: Path.Combine(stage, PluginRelease.PluginFile),
            opcodesDir: ExistingOrNull(ws.OpcodesDir),
            overridesDir: ExistingOrNull(ws.OverridesDir),
            dataDir: ExistingOrNull(dataDir),
            signaturesDir: sigDir);

        // 4) 플러그인 폴더의 부속 DLL(베이스 플러그인, 방금 패치해 쓴 deucalion 제외)을 같이 넣는다
        foreach (string f in Directory.GetFiles(pluginDir, "*.dll"))
        {
            string name = Path.GetFileName(f);
            if (name.Equals(PluginRelease.PluginFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (written.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            File.Copy(f, Path.Combine(stage, name), overwrite: true);
            Log.Section($@"[copy] {name} -> output\");
        }

        var info = new BuildInfo(DateTime.Now, MaterialState.PluginVersionOf(pluginPath),
            // 재료를 만든 게임 버전 (prepare 기록). 기록이 없으면 지금 클라이언트 버전
            state?.GameVersions ?? clients.Where(c => c.Region != null && c.Version != null).ToDictionary(c => c.Region!.Name, c => c.Version!),
            state?.PatchNeeded);
        info.Save(stage);
        var files = Directory.GetFiles(stage, "*.dll").Select(Path.GetFileName).ToList();

        Directory.CreateDirectory(outDir);
        foreach (string f in Directory.GetFiles(outDir, "*.dll")) File.Delete(f);
        foreach (string f in Directory.GetFiles(stage))
            File.Move(f, Path.Combine(outDir, Path.GetFileName(f)), overwrite: true);
        Directory.Delete(stage, recursive: true);

        Log.Done($"[done] -> {outDir}");
        return new BuildResult(outDir, files!, warnings, info);
    }

    // 플러그인 폴더의 deucalion 이 플러그인(Machina _checksum)과 짝인지 — 아니면 ACT 에서 패킷을 못 받는다
    private static void CheckDeucalionPair(string pluginPath)
    {
        if (!File.Exists(pluginPath)) return; // repack 이 알아듣게 알린다
        string? distrib = Deucalion.FindDistrib(Path.GetDirectoryName(pluginPath)!);
        if (distrib == null) return;
        using var plugin = Repacker.ReadPlugin(pluginPath);
        using var machina = Costura.ReadInner(plugin, Costura.Machina);
        string? expected = DeucalionMachina.ReadChecksum(machina);
        if (expected != null && expected != DeucalionMachina.FormatChecksum(Deucalion.Sha256(File.ReadAllBytes(distrib))))
            throw new ToolException($"{Path.GetFileName(distrib)} 이(가) 이 플러그인의 deucalion 이 아닙니다 (Machina _checksum 불일치). " +
                                    "플러그인과 같은 릴리스의 deucalion 만 같은 폴더에 두세요.");
    }

    private static string? ExistingOrNull(string dir) => Directory.Exists(dir) ? dir : null;

    // 클라이언트 하나라도 0건이면 빌드 중단 — 안 맞는 시그니처로는 그 기능이 확실히 실패한다.
    // memory/oodle 은 그 클라이언트가 쓰는 변형에 해당하는 것만 본다. 클라이언트 설정이 없으면 검증 없이 진행.
    private static void VerifySignatures(List<GameClient> clients, string? sigDir)
    {
        // (종류, 변형, 이름, 패턴) — memory/oodle 은 변형별로 실제 쓰일 값(변형 줄 우선)
        var inputs = SignatureInput.Load(sigDir).Select(s => (Kind: "deucalion", Variant: "*", s.Name, s.Pattern)).ToList();
        inputs.AddRange(SignatureInput.Resolve(SignatureInput.Load(sigDir, SignatureInput.MemoryFile), PluginSignatures.MemoryVariants)
            .Select(x => (Kind: "memory", x.Variant, x.Name, x.Pattern)));
        inputs.AddRange(SignatureInput.Resolve(SignatureInput.Load(sigDir, SignatureInput.OodleFile), PluginSignatures.OodleVariants)
            .Select(x => (Kind: "oodle", x.Variant, x.Name, x.Pattern)));
        if (inputs.Count == 0) return;
        if (clients.Count == 0)
        {
            Log.Note($"[sig] {Config.FileName} 에 clients 없음 - 교체 시그니처 검증 생략");
            return;
        }
        var broken = new List<string>();
        foreach (var client in clients)
        {
            var exe = new ScanTarget(client.ExePath);
            foreach (var (kind, variant, name, pattern) in inputs)
            {
                if (!Sigcheck.Applies(kind, variant, client.Region)) continue;
                int n = SigPattern.Parse(pattern).Scan(exe).Count;
                string line = $"[sig] {client.Name} {(kind == "deucalion" ? "" : $"{kind}.{variant}.")}{name}: {n}곳";
                if (n == 0) { Log.Error(line); broken.Add($"{client.Name}/{name}"); }
                // 플러그인 메모리/Oodle 은 첫 번째로 맞는 곳만 쓴다 — 여러 곳이면 그 첫 곳이 맞는지 확인할 것
                else if (n > 1 && kind != "deucalion") Log.Warn(line + " - 여러 곳에 맞음, 플러그인은 첫 번째 위치만 씀");
                else Log.Section(line);
            }
        }
        if (broken.Count > 0)
            throw new ToolException($"게임 exe 에서 맞는 위치를 찾지 못한 시그니처가 있습니다: {string.Join(", ", broken)}");
    }

}
