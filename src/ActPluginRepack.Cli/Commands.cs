// 명령마다: 옵션 해석 -> Core 호출 -> 결과 출력 -> 종료 코드
internal static class Commands
{
    public static int Status(string[] args)
    {
        var o = Options.Parse(args, ["--root"]);
        var ws = o.Workspace();
        var s = global::Status.Collect(ws);

        // 이름표는 GUI 의 상태 칸과 같은 칸 수로 (한글은 두 칸)
        static string Line(string label, string text) => "  " + StatusLine.Format(label, text);
        Log.Section($"[status] {ws.Root}");
        foreach (var line in s.Describe()) Log.Write(line.Kind, "  " + line.Format());
        var (usageOk, usage) = GitHub.TryDescribeUsage(brief: true);
        Log.Write(usageOk ? LogKind.Info : LogKind.Note, Line("GitHub API", usage));
        Log.Info(Line("이 도구", AppInfo.DisplayVersion));
        if (AppInfo.CheckForUpdate() is { } update) Log.Warn(Line("새 버전", $"{update.Tag} - {update.Url}"));
        foreach (string a in s.Advice) Log.Done($"=> {a}");
        return ExitCodes.Ok;
    }

    // 마지막 prepare 의 출처 보고 (extras\sources.txt 와 같은 내용)
    public static int Sources(string[] args)
    {
        var o = Options.Parse(args, ["--root"]);
        var ws = o.Workspace();
        var report = SourceReport.Load(ws) ?? throw new ToolException(File.Exists(SourceReport.PathOf(ws))
            ? $"출처 보고({SourceReport.PathOf(ws)})를 읽지 못했습니다. prepare 를 다시 실행하면 새로 만듭니다."
            : "출처 보고가 없습니다. 먼저 prepare 를 실행하세요.");
        Console.Write(report.ToText());
        return ExitCodes.Ok;
    }

    // 패치본 배포 묶음 (zip + 릴리스 설명). 올리는 건 출력한 gh 명령으로 직접.
    public static int Hotfix(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--tag"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        var r = global::HotfixPackage.Create(ws, o.Get("--tag"));
        Log.Info($"[hotfix] 담은 파일: {string.Join(", ", r.Files)}");
        Log.Info("[hotfix] release-notes.md 의 '알려진 문제' 를 확인한 뒤 올리기:");
        Log.Info($"  {r.UploadCommand}");
        return ExitCodes.Ok;
    }

    // (배포하는 사람용) 자동 업데이트 서명 키 만들기. 공개키는 Core\Infrastructure\UpdateSignature.cs 에 넣는다.
    public static int ReleaseKeygen(string[] args)
    {
        var o = Options.Parse(args, ["--out"]);
        string path = Path.GetFullPath(o.Get("--out") ?? UpdateSignature.DefaultKeyPath);
        if (File.Exists(path)) throw new ToolException($"이미 키 파일이 있습니다: {path}. 새로 만들면 이전 키로 서명한 버전과 맞지 않게 되므로 덮어쓰지 않습니다.");
        var (privatePem, publicKey) = UpdateSignature.Generate();
        Files.WriteAtomic(path, privatePem);
        Log.Done($"[release-keygen] 개인키: {path} (저장소에 넣지 말고 따로 백업할 것)");
        Log.Info($"[release-keygen] 공개키 (UpdateSignature.PublicKey 에): {publicKey}");
        return ExitCodes.Ok;
    }

    // (배포하는 사람용) 배포 zip 에 서명 -> <zip>.sig (publish.ps1 이 부른다)
    public static int ReleaseSign(string[] args)
    {
        var o = Options.Parse(args, ["--zip", "--key"]);
        string zip = Path.GetFullPath(o.Need("--zip"));
        if (!File.Exists(zip)) throw new ToolException($"zip 파일이 없습니다: {zip}");
        string keyPath = o.Get("--key") ?? UpdateSignature.DefaultKeyPath;
        if (!File.Exists(keyPath))
            throw new ToolException($"서명 키가 없습니다: {keyPath}. 처음이면 release-keygen 으로 만들고 공개키를 UpdateSignature.cs 에 넣으세요.");
        string sha = Files.Sha256Of(zip);
        string sig = UpdateSignature.Sign(File.ReadAllText(keyPath), Path.GetFileName(zip), sha);
        if (!UpdateSignature.Verify(Path.GetFileName(zip), sha, sig))
            throw new ToolException("이 키는 프로그램에 들어 있는 공개키와 짝이 아닙니다. UpdateSignature.PublicKey 를 확인하세요.");
        Files.WriteAtomic(zip + ".sig", sig + "\n");
        Log.Done($"[release-sign] 서명: {zip}.sig");
        return ExitCodes.Ok;
    }
    // ACT 로그의 RSV(OverlayPlugin 262 줄) -> input\overrides\global_rsv.txt (공식 플러그인에 없는 것만)
    public static int RsvImport(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--logs", "--days"]);
        var ws = o.Workspace();
        int days = o.Get("--days") is { } d ? int.TryParse(d, out int n) && n > 0 ? n : throw new ToolException("--days 에는 1 이상의 정수를 쓰세요.") : Rsv.DefaultDays;
        var files = new List<string>();
        string[] logs = o.GetList("--logs") is { Length: > 0 } given ? given : [Rsv.LogFolder()];
        foreach (string p in logs)
        {
            if (File.Exists(p)) files.Add(Path.GetFullPath(p));
            else if (Directory.Exists(p)) files.AddRange(Rsv.RecentLogs(p, days));
            else throw new ToolException($"로그 파일이나 폴더가 없습니다: {p}");
        }
        if (files.Count == 0)
            throw new ToolException($"최근 {days}일 안의 ACT 로그가 없습니다 ({string.Join(", ", logs)}). " +
                                    "OverlayPlugin 을 켜고 RSV 가 있는 콘텐츠에 들어간 뒤 다시 실행하세요.");
        using var _ = ws.Lock();
        var r = Rsv.Import(ws, files);
        return r.Conflicts.Count > 0 ? ExitCodes.RsvConflicts : ExitCodes.Ok;
    }

    // GitHub API 사용량과 토큰. --token <토큰 | -> 저장 ('-' 면 표준 입력에서 읽음 — 명령 기록에 남지 않게), --clear 삭제
    public static int GitHubToken(string[] args)
    {
        var o = Options.Parse(args, ["--token"], ["--clear"]);
        if (o.Flag("--clear"))
        {
            GitHub.DeleteToken();
            Log.Done("[github] 저장한 토큰을 지움");
        }
        if (o.Get("--token") is { } token)
        {
            if (token == "-") token = Console.In.ReadLine() ?? "";
            var saved = GitHub.SaveToken(token);
            Log.Done($"[github] 토큰 저장 - 시간당 {saved.Limit}회");
        }
        var current = GitHub.Token;
        Log.Info($"[github] 토큰: {current?.Source ?? $"없음 (시간당 {GitHub.AnonymousLimit}회)"}" + (GitHub.HasSavedToken ? "  (저장한 토큰 있음)" : ""));
        if (GitHub.Query() is { } limit) Log.Info($"[github] 사용량: {GitHub.Describe(limit, current?.Source)}");
        if (current == null) Log.Note($"[github] 권한 없는 토큰이면 충분: {GitHub.TokenPageUrl}");
        return ExitCodes.Ok;
    }

    // 설치된 게임 클라이언트 찾기. --save 면 지역을 알아낸 것을 config.json 의 clients 로 저장
    public static int Detect(string[] args)
    {
        var o = Options.Parse(args, ["--root"], ["--save"]);
        var found = ClientLocator.Detect();
        if (found.Count == 0) Log.Warn("[detect] 설치된 게임 클라이언트를 찾지 못함 - config.json 에 직접 적을 것");
        foreach (var c in found)
            Log.Info($"[detect] {c.Region?.Name ?? "(지역 모름)",-20} {c.Version ?? "?",-22} {c.InstallDir}  ({c.Source})");
        if (!o.Flag("--save")) return ExitCodes.Ok;

        var ws = o.Workspace();
        // 한국·글로벌만 저장한다 (중국·대만판은 찾아도 이미 적어 둔 것만 — ClientLocator.SelectedByDefault)
        var configured = Config.RawClients(ws.Root).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in found.Where(c => c.Region != null && !ClientLocator.SelectedByDefault(c.Region) && !configured.Contains(c.Region.Name)))
            Log.Note($"[detect] {c.Region!.Name} 은(는) 찾았지만 저장하지 않음 (쓰려면 config.json 에 적거나 화면에서 체크): {c.InstallDir}");
        var known = found.Where(c => ClientLocator.SelectedByDefault(c.Region) || c.Region != null && configured.Contains(c.Region.Name))
            .GroupBy(c => c.Region!.Name).ToList();
        foreach (var g in known.Where(g => g.Count() > 1))
            Log.Warn($"[detect] {g.Key} 가 여러 개 - 첫 번째를 씀: {g.First().InstallDir}");
        // 예전 판별로 다른 지역 이름에 적힌 경로 (중국판이 global 로 — 런처가 같은 ffxivboot.exe 라 예전에는 글로벌로 보였다)
        var mislabeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (region, dir) in Config.RawClients(ws.Root))
        {
            string install = File.Exists(dir) ? Path.GetDirectoryName(Path.GetDirectoryName(dir)!)! :
                Path.GetFileName(dir.TrimEnd('\\', '/')).Equals("game", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir.TrimEnd('\\', '/'))! : dir;
            if (Region.Find(region) is { } written && ClientLocator.InferRegion(install) is { } actual && ClientLocator.Contradicts(actual, written))
            {
                Log.Warn($"[detect] config.json 의 {region} 경로는 {actual.Name} 클라이언트로 보입니다 - 확인하세요: {dir}");
                mislabeled.Add(region);
            }
        }
        if (known.Count == 0)
            throw new ToolException("저장할 한국·글로벌 클라이언트를 찾지 못했습니다 (중국·대만판은 config.json 에 직접 적거나 화면에서 체크).");
        using var _ = ws.Lock();
        var clients = known.ToDictionary(g => g.Key, g => g.First().InstallDir, StringComparer.OrdinalIgnoreCase);
        // 찾지 못한 지역(지역을 알 수 없는 대만판을 손으로 적어 둔 것 등)은 그대로 둔다
        foreach (var (region, dir) in Config.RawClients(ws.Root).Where(x => !clients.ContainsKey(x.Key)))
        {
            clients[region] = dir;
            if (!mislabeled.Contains(region)) Log.Note($"[detect] {region} 은(는) 이번에 저장하지 않아 적어 둔 경로를 그대로 둠: {dir}");
        }
        Config.SetClients(ws.Root, clients);
        Log.Done($"[detect] {Config.FileName} 의 clients 저장: {string.Join(", ", clients.Keys)}");
        return ExitCodes.Ok;
    }

    public static int Prepare(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin", "--sources", "--ida-target"],
            ["--no-plugin", "--no-csv", "--no-opcodes", "--no-snapshot", "--no-sigcheck", "--no-repair", "--ida",
             "--overwrite", "--server-exe", "--all-regions", "--cross-check"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        var r = global::Prepare.Run(ws, new PrepareOptions
        {
            ServerExe = o.Flag("--server-exe"),
            ServerAllRegions = o.Flag("--all-regions"),
            CrossCheck = o.Flag("--cross-check"),
            Plugin = o.Get("--plugin"),
            UpdatePlugin = !o.Flag("--no-plugin"),
            Snapshot = !o.Flag("--no-snapshot"),
            Sigcheck = !o.Flag("--no-sigcheck"),
            Repair = !o.Flag("--no-repair"),
            Opcodes = !o.Flag("--no-opcodes"),
            Csv = !o.Flag("--no-csv"),
            ForceIda = o.Flag("--ida"),
            IdaTarget = o.Get("--ida-target"),
            Overwrite = o.Flag("--overwrite"),
            Sources = o.GetList("--sources"),
        });

        Log.Info();
        Log.Section("[prepare] 요약");
        foreach (var step in r.Steps) Log.Write(step.SummaryKind, step.SummaryLine());
        if (r.PatchNeeded == false) Log.Done($"[prepare] {r.Verdict}");
        else Log.Section($"[prepare] {r.Verdict}");
        if (r.PatchNeeded == true && r.UnconfirmedOpcodes.Count > 0)
            Log.Warn($"[prepare] {string.Join(", ", r.UnconfirmedOpcodes.Select(x => x.Name))} 의 opcode 는 이번 버전 것으로 확인된 공개 자료 없이 정한 값입니다 (직접 구함 또는 확인 안 된 karashiiro) - " +
                     "큰 패치에서는 틀릴 수 있으니 공개되면(보통 몇 시간 안) prepare 를 다시 실행하세요.");
        if (r.Outcome == PrepareOutcome.OpcodesMissing)
            Log.Warn($"[prepare] opcode 를 못 구한 클라이언트: {string.Join(", ", r.MissingOpcodes)} - " +
                     (r.OpcodeProblem ?? "아직 공개 전일 수 있으니 나중에 다시 실행하세요. 그대로 빌드하면 패킷 해석이 틀릴 수 있음"));
        // 0 은 "패치본을 만들 차례"일 때만 — 스크립트(build.ps1)가 그대로 빌드·설치해도 되게.
        // 판단에 필요한 단계를 직접 껐으면(--no-sigcheck / --no-opcodes) 판단이 없어도 실패가 아니다 (예전처럼 0)
        // (출처에 빠진 항목 때문이면 끈 단계와 상관없는 진짜 판단 불가)
        bool skipped = (o.Flag("--no-sigcheck") || o.Flag("--no-opcodes")) && r.Verdict != global::Prepare.MissingItemsVerdict;
        return r.Outcome switch
        {
            PrepareOutcome.HooksBroken => ExitCodes.HooksBroken,
            PrepareOutcome.OpcodesMissing => ExitCodes.Incomplete,
            PrepareOutcome.NotNeeded => ExitCodes.NotNeeded,
            PrepareOutcome.Undecided => skipped ? ExitCodes.Ok : ExitCodes.Undecided,
            _ => ExitCodes.Ok,
        };
    }

    public static int Build(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin"], ["--no-csv"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        global::Build.Run(ws, new BuildOptions { Plugin = o.Get("--plugin"), Csv = !o.Flag("--no-csv") });
        return ExitCodes.Ok;
    }

    // ACT 폴더에 쓸 권한이 없으면 관리자 권한으로 다시 실행한다. 대상 폴더는 여기서 미리 정해 넘긴다
    // (다른 관리자 계정으로 올라가면 %APPDATA% 가 달라 ACT 설정을 못 찾을 수 있으므로).
    public static int Install(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--target", "--overlay", Elevated.LogOption], ["--official"]);
        var ws = o.Workspace();
        return Elevated.RunLogged(o.Get(Elevated.LogOption), () =>
        {
            var plan = global::Install.Plan(ws, o.GetAll("--target") is { Count: > 0 } targets ? targets : null, o.Get("--overlay"), official: o.Flag("--official"));
            try
            {
                using var _ = ws.Lock();
                global::Install.Run(ws, plan);
                return ExitCodes.Ok;
            }
            catch (ElevationRequiredException ex) when (o.Get(Elevated.LogOption) == null)
            {
                return Elevated.Relaunch("install", plan.ToCliArgs(ws), ex.Message);
            }
        });
    }

    public static int Restore(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--backup", Elevated.LogOption]);
        var ws = o.Workspace();
        return Elevated.RunLogged(o.Get(Elevated.LogOption), () =>
        {
            // 관리자 권한으로 다시 실행하면 현재 폴더가 바뀌므로 절대 경로로
            string backup = o.Get("--backup") is { } given ? Path.GetFullPath(given, ws.Root) // 상대 경로는 작업 폴더 기준
                : global::Install.LatestBackup(ws) ?? throw new ToolException(@"backups\ 에 되돌릴 백업이 없습니다.");
            try
            {
                using var _ = ws.Lock();
                global::Install.Restore(ws, backup);
                return ExitCodes.Ok;
            }
            catch (ElevationRequiredException ex) when (o.Get(Elevated.LogOption) == null)
            {
                return Elevated.Relaunch("restore", ["--root", ws.Root, "--backup", backup], ex.Message);
            }
        });
    }

    public static int Sigcheck(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin", "--game", "--signatures"]);
        var ws = o.Workspace();
        var clients = o.Get("--game") != null ? Config.FromPaths(o.GetList("--game")) : Config.Load(ws.Root).Clients;
        if (clients.Count == 0)
            throw new ToolException($"설정된 게임 클라이언트가 없습니다. {Config.FileName} 의 \"clients\" 를 설정하거나 --game <dir> 로 지정하세요.");
        var r = global::Sigcheck.Execute(Path.GetFullPath(o.Get("--plugin") ?? ws.PluginPath),
                                         o.Get("--signatures") ?? ws.SignaturesDir, clients);
        return r.CriticalOk ? ExitCodes.Ok : ExitCodes.HooksBroken;
    }

    public static int Repack(string[] args)
    {
        // --out 이 다른 명령과 같은 이름, --output 은 예전 이름 (계속 받는다)
        var o = Options.Parse(args, ["--root", "--plugin", "--out", "--output", "--opcodes", "--overrides", "--data", "--signatures"]);
        if (o.Get("--out") != null && o.Get("--output") != null) throw new ToolException("--out 과 --output 은 같은 옵션입니다. 하나만 쓰세요.");
        var ws = o.Workspace();
        using var _ = ws.Lock();
        Repacker.Execute(
            pluginPath: o.Get("--plugin") ?? ws.PluginPath,
            outputPath: o.Get("--out") ?? o.Get("--output") ?? ws.OutputPluginPath,
            opcodesDir: StageDir(o, "--opcodes", ws.OpcodesDir),
            overridesDir: StageDir(o, "--overrides", ws.OverridesDir),
            dataDir: StageDir(o, "--data", ws.DataDir),
            signaturesDir: StageDir(o, "--signatures", ws.SignaturesDir));
        return ExitCodes.Ok;
    }

    // '-' = 명시적 생략. 명시된 폴더가 없으면 에러, 기본 폴더가 없으면 자동 생략(null).
    private static string? StageDir(Options o, string opt, string fallback)
    {
        string? dir = o.Get(opt);
        if (dir == "-") return null;
        if (dir != null)
        {
            if (!Directory.Exists(dir)) throw new ToolException($"폴더가 없습니다: {dir}");
            return Path.GetFullPath(dir);
        }
        return Directory.Exists(fallback) ? Path.GetFullPath(fallback) : null;
    }

    public static int Csv(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--csv-dir", "--out", "--lang", "--lists"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        CsvConverter.Convert(
            csvDir: o.Get("--csv-dir") ?? ws.CsvLeafDir("ko"),
            outDir: o.Get("--out") ?? ws.DataDir,
            lang: o.Get("--lang"),
            o.GetList("--lists") is { Length: > 0 } l ? l : CsvConverter.DefaultLists);
        return ExitCodes.Ok;
    }

    public static int Export(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin", "--what", "--out", "--filter"], ["--force"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        Exporter.Export(ws, o.Get("--what", "data"), o.Get("--plugin"), o.Get("--out"), o.GetList("--filter"), o.Flag("--force"));
        return ExitCodes.Ok;
    }

    // 공식 패치 서버(Thaliak)에서 특정 버전의 ffxiv_dx11.exe 를 snapshots\ 로 받는다
    public static int FetchExe(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--region", "--version", "--patch", "--out"]);
        var region = o.Region("--region", "global");
        if (o.Get("--patch") is { } patch)
        {
            // 패치 파일 직접 지정 (예전 버전은 패치 이름과 게임 버전이 어긋나서)
            string outPath = o.Need("--out");
            string url = Thaliak.PatchUrlByName(region, patch);
            Log.Note($"[fetch-exe] {url}");
            byte[] exe = Thaliak.DownloadExe(url, outPath);
            Log.Done($"[fetch-exe] {outPath} (build {Thaliak.BuildDate(exe) ?? "?"}, {exe.Length:N0} bytes)");
            return ExitCodes.Ok;
        }
        if (o.Get("--out") != null)
            throw new ToolException(@"--out 은 --patch 와 함께만 씁니다. --version 으로 받은 exe 는 작업 폴더의 snapshots\ 에 둡니다.");
        var ws = o.Workspace();
        using var _ = ws.Lock();
        string version = SigCommands.GameVersionArg(o.Need("--version"));
        Log.Done($"[fetch-exe] {Thaliak.SnapshotExe(ws, region, version)}");
        return ExitCodes.Ok;
    }

    // (개발용) 지난 패치 재현: 그 출시를 패치 당일 오전 조건으로 다시 세워 prepare -> build 하고 공식 릴리스(정답)와 비교.
    // 작업 폴더는 따로(%TEMP% 아래) — 사용자 작업 폴더를 쓰지 않으므로 --root 도 없다. --version 이 없으면 최근 출시 목록만.
    public static int Replay(string[] args)
    {
        var o = Options.Parse(args, ["--version", "--region", "--base", "--ref", "--sources", "--work", "--tools"], ["--keep"]);
        var region = o.Region("--region", "global");
        if (o.Get("--version") is not { } version)
        {
            Log.Section($"[replay] {region.Name} 최근 출시 (하나를 --version 으로)");
            foreach (string v in Thaliak.Releases(region).TakeLast(8).Reverse())
                Log.Info($"  {v}  {Thaliak.ReleasedAt(region, v):yyyy-MM-dd HH:mm} UTC 출시");
            return ExitCodes.Ok;
        }
        var r = global::Replay.Run(new ReplayOptions
        {
            Region = region,
            Version = version,
            BaseTag = o.Get("--base"),
            RefTag = o.Get("--ref"),
            Sources = o.GetList("--sources") is { Length: > 0 } sources ? sources : ReplayOptions.DefaultSources,
            WorkDir = o.Get("--work"),
            ToolsDir = o.Get("--tools"),
            Keep = o.Flag("--keep"),
        });
        global::Replay.Report(r);
        return r.Pass ? ExitCodes.Ok : ExitCodes.Error;
    }

    // 문제 보고용 기록 묶음 (GUI 의 [문제 보고용 기록 저장]과 같은 zip). 작업 폴더는 읽기만 하므로 잠그지 않는다.
    public static int Report(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--out"]);
        var ws = o.Workspace();
        string name = global::ReportBundle.DefaultName(DateTime.Now);
        string zip = o.Get("--out") is { } given ? Directory.Exists(given) ? Path.Combine(given, name) : given : name;
        zip = Path.GetFullPath(zip);
        var entries = global::ReportBundle.Create(ws, zip);
        foreach (string entry in entries) Log.Note($"  {entry}");
        Log.Done($"[report] {zip} ({entries.Count}개 항목)");
        Log.Info($"[report] 이 파일을 디스코드({AppInfo.DiscordUrl})에 올리고 어떤 문제인지 적어 주세요. " +
                 "기록에는 이 PC 의 폴더 경로가 들어 있어 Windows 사용자 이름이 보일 수 있습니다.");
        return ExitCodes.Ok;
    }

    // (개발용) 두 exe 의 함수를 명령어 모양 단위로 나란히 비교
    public static int FnDiff(string[] args)
    {
        var o = Options.Parse(args, ["--old", "--new", "--old-rva", "--new-rva"]);
        var oldExe = SigLib.Code(o.Need("--old"));
        var newExe = SigLib.Code(o.Need("--new"));
        var a = oldExe.FunctionTokens(Rva(o.Need("--old-rva")));
        var b = newExe.FunctionTokens(Rva(o.Need("--new-rva")));
        Log.Info($"old {a.Length} / new {b.Length} instructions");
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string x = i < a.Length ? a[i] : "", y = i < b.Length ? b[i] : "";
            Log.Info($"{(x == y ? "  " : "!!")} {i,4} {x,-40} {y}");
        }
        return ExitCodes.Ok;

        static int Rva(string s) =>
            int.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, System.Globalization.NumberStyles.HexNumber, null, out int v)
                ? v : throw new ToolException($"RVA 는 16진수입니다: '{s}'");
    }
}
