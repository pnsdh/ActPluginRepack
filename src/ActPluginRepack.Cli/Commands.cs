// 명령마다: 옵션 해석 -> Core 호출 -> 결과 출력 -> 종료 코드
internal static class Commands
{
    public static int Status(string[] args)
    {
        var o = Options.Parse(args, ["--root"]);
        var ws = o.Workspace();
        var s = global::Status.Collect(ws);

        // 이름표는 12칸 (한글은 두 칸)
        static string Line(string label, string text) => "  " + StatusLine.Pad(label, 12) + text;
        Log.Section($"[status] {ws.Root}");
        foreach (var line in s.Describe()) Log.Write(line.Kind, Line(line.Label, line.Text));
        try
        {
            if (GitHub.Query() is { } limit) Log.Info(Line("GitHub API", GitHub.Describe(limit, GitHub.Token?.Source)));
        }
        catch (ToolException ex) { Log.Note(Line("GitHub API", $"사용량을 확인하지 못함: {ex.Message}")); }
        Log.Info(Line("이 도구", AppInfo.DisplayVersion));
        if (AppInfo.CheckForUpdate() is { } update) Log.Warn(Line("새 버전", $"{update.Tag} - {update.Url}"));
        foreach (string a in s.Advice) Log.Done($"=> {a}");
        return 0;
    }

    // 패치본 배포 묶음 (zip + 릴리스 설명). 올리는 건 출력한 gh 명령으로 직접.
    public static int Hotfix(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--tag"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        var r = global::HotfixPackage.Create(ws, o.Get("--tag"));
        Log.Info($"  담은 파일: {string.Join(", ", r.Files)}");
        Log.Info("  release-notes.md 의 '알려진 문제' 를 확인한 뒤 올리기:");
        Log.Info($"  {r.UploadCommand}");
        return 0;
    }

    // (배포하는 사람용) 자동 업데이트 서명 키 만들기. 공개키는 Core\Infrastructure\UpdateSignature.cs 에 넣는다.
    public static int ReleaseKeygen(string[] args)
    {
        var o = Options.Parse(args, ["--out"]);
        string path = Path.GetFullPath(o.Get("--out") ?? UpdateSignature.DefaultKeyPath);
        if (File.Exists(path)) throw new ToolException($"이미 키 파일이 있습니다: {path}. 새로 만들면 이전 키로 서명한 버전과 맞지 않게 되므로 덮어쓰지 않습니다.");
        var (privatePem, publicKey) = UpdateSignature.Generate();
        Files.WriteAtomic(path, privatePem);
        Log.Done($"[release] 개인키: {path} (저장소에 넣지 말고 따로 백업할 것)");
        Log.Info($"[release] 공개키 (UpdateSignature.PublicKey 에): {publicKey}");
        return 0;
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
        Log.Done($"[release] 서명: {zip}.sig");
        return 0;
    }
    // ACT 로그의 RSV(OverlayPlugin 262 줄) -> input\overrides\global_rsv.txt (공식 플러그인에 없는 것만)
    public static int RsvImport(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--logs", "--days"]);
        var ws = o.Workspace();
        int days = o.Get("--days") is { } d ? int.TryParse(d, out int n) && n > 0 ? n : throw new ToolException("--days 에는 1 이상의 정수를 쓰세요.") : Rsv.DefaultDays;
        var files = new List<string>();
        foreach (string p in o.GetList("--logs").DefaultIfEmpty(Rsv.LogFolder()))
        {
            if (File.Exists(p)) files.Add(Path.GetFullPath(p));
            else if (Directory.Exists(p)) files.AddRange(Rsv.RecentLogs(p, days));
            else throw new ToolException($"로그 파일이나 폴더가 없습니다: {p}");
        }
        if (files.Count == 0)
            throw new ToolException($"최근 {days}일 안의 ACT 로그가 없습니다 ({string.Join(", ", o.GetList("--logs").DefaultIfEmpty(Rsv.LogFolder()))}). " +
                                    "OverlayPlugin 을 켜고 RSV 가 있는 콘텐츠에 들어간 뒤 다시 실행하세요.");
        using var _ = ws.Lock();
        var r = Rsv.Import(ws, files);
        return r.Conflicts.Count > 0 ? 2 : 0;
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
        Log.Info($"[github] 토큰: {current?.Source ?? "없음 (시간당 60회)"}" + (GitHub.HasSavedToken ? "  (저장한 토큰 있음)" : ""));
        if (GitHub.Query() is { } limit) Log.Info($"[github] 사용량: {GitHub.Describe(limit, current?.Source)}");
        if (current == null) Log.Note($"[github] 권한 없는 토큰이면 충분: {GitHub.TokenPageUrl}");
        return 0;
    }

    // 설치된 게임 클라이언트 찾기. --save 면 지역을 알아낸 것을 config.json 의 clients 로 저장
    public static int Detect(string[] args)
    {
        var o = Options.Parse(args, ["--root"], ["--save"]);
        var found = ClientLocator.Detect();
        if (found.Count == 0) Log.Warn("[detect] 설치된 게임 클라이언트를 찾지 못함 - config.json 에 직접 적을 것");
        foreach (var c in found)
            Log.Info($"  {c.Region?.Name ?? "(지역 모름)",-20} {c.Version ?? "?",-22} {c.InstallDir}  ({c.Source})");
        if (!o.Flag("--save")) return 0;

        var ws = o.Workspace();
        var known = found.Where(c => c.Region != null).GroupBy(c => c.Region!.Name).ToList();
        foreach (var g in known.Where(g => g.Count() > 1))
            Log.Warn($"[detect] {g.Key} 가 여러 개 - 첫 번째를 씀: {g.First().InstallDir}");
        if (known.Count == 0) throw new ToolException("지역을 알아낸 클라이언트가 없어 저장하지 않았습니다.");
        using var _ = ws.Lock();
        Config.SetClients(ws.Root, known.ToDictionary(g => g.Key, g => g.First().InstallDir));
        Log.Done($"[detect] {Config.FileName} 의 clients 저장: {string.Join(", ", known.Select(g => g.Key))}");
        return 0;
    }

    public static int Prepare(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin", "--sources", "--ida-target"],
            ["--no-plugin", "--no-csv", "--no-opcodes", "--no-snapshot", "--no-sigcheck", "--no-repair", "--ida", "--localdiff",
             "--overwrite"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        var r = global::Prepare.Run(ws, new PrepareOptions
        {
            Plugin = o.Get("--plugin"),
            UpdatePlugin = !o.Flag("--no-plugin"),
            Snapshot = !o.Flag("--no-snapshot"),
            Sigcheck = !o.Flag("--no-sigcheck"),
            Repair = !o.Flag("--no-repair"),
            Opcodes = !o.Flag("--no-opcodes"),
            Csv = !o.Flag("--no-csv"),
            ForceIda = o.Flag("--ida"),
            IdaTarget = o.Get("--ida-target"),
            ForceLocalDiff = o.Flag("--localdiff"),
            Overwrite = o.Flag("--overwrite"),
            Sources = o.GetList("--sources"),
        });

        Log.Info();
        Log.Section("[prepare] 요약");
        foreach (var step in r.Steps)
        {
            string line = $"  {(step.Status == StepStatus.Ok ? "OK" : "!!"),-4} {step.Name,-30} {step.Message}";
            if (step.Status == StepStatus.Ok) Log.Done(line); else Log.Warn(line);
        }
        if (r.PatchNeeded == false) Log.Done($"[prepare] {r.Verdict}");
        else Log.Section($"[prepare] {r.Verdict}");
        if (!r.HooksOk) return 2;
        if (r.MissingOpcodes.Count > 0)
        {
            Log.Warn($"[prepare] opcode 를 못 구한 클라이언트: {string.Join(", ", r.MissingOpcodes)} - 그대로 빌드하면 패킷 해석이 틀릴 수 있음");
            return 3;
        }
        return 0;
    }

    public static int Build(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin"], ["--no-csv"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        global::Build.Run(ws, new BuildOptions { Plugin = o.Get("--plugin"), Csv = !o.Flag("--no-csv") });
        return 0;
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
                return 0;
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
                return 0;
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
        return r.CriticalOk ? 0 : 2;
    }

    public static int Repack(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin", "--output", "--opcodes", "--overrides", "--data", "--signatures"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        Repacker.Execute(
            pluginPath: o.Get("--plugin") ?? ws.PluginPath,
            outputPath: o.Get("--output") ?? ws.OutputPluginPath,
            opcodesDir: StageDir(o, "--opcodes", ws.OpcodesDir),
            overridesDir: StageDir(o, "--overrides", ws.OverridesDir),
            dataDir: StageDir(o, "--data", ws.DataDir),
            signaturesDir: StageDir(o, "--signatures", ws.SignaturesDir));
        return 0;
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
        return 0;
    }

    public static int Export(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin", "--what", "--out", "--filter"], ["--force"]);
        var ws = o.Workspace();
        using var _ = ws.Lock();
        Exporter.Export(ws, o.Get("--what", "data"), o.Get("--plugin"), o.Get("--out"), o.GetList("--filter"), o.Flag("--force"));
        return 0;
    }

    // 공식 패치 서버(Thaliak)에서 특정 버전의 ffxiv_dx11.exe 를 snapshots\ 로 받는다
    public static int FetchExe(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--region", "--version", "--patch", "--out"]);
        var region = Region.Find(o.Get("--region", "global"))
                     ?? throw new ToolException($"알 수 없는 지역입니다. {string.Join(" / ", Region.All.Select(r => r.Name))} 중 하나를 쓰세요.");
        if (o.Get("--patch") is { } patch)
        {
            // 패치 파일 직접 지정 (예전 버전은 패치 이름과 게임 버전이 어긋나서)
            string outPath = o.Need("--out");
            string url = Thaliak.PatchUrlByName(region, patch);
            Log.Note($"[fetch-exe] {url}");
            byte[] exe = Thaliak.DownloadExe(url, outPath);
            Log.Done($"[fetch-exe] {outPath} (build {Thaliak.BuildDate(exe) ?? "?"}, {exe.Length:N0} bytes)");
            return 0;
        }
        if (o.Get("--out") != null)
            throw new ToolException(@"--out 은 --patch 와 함께만 씁니다. --version 으로 받은 exe 는 작업 폴더의 snapshots\ 에 둡니다.");
        var ws = o.Workspace();
        using var _ = ws.Lock();
        string version = GameVersions.VersionOf(o.Need("--version")) ?? o.Need("--version"); // 패치 번호(7.56h)도 받는다
        Log.Done($"[fetch-exe] {Thaliak.SnapshotExe(ws, region, version)}");
        return 0;
    }

    // 옛 exe 에서 맞던 시그니처를 새 exe 용으로 다시 만든다 (--truth: 정답 시그니처와 같은 곳을 가리키는지 비교)
    public static int SigRegen(string[] args)
    {
        var o = Options.Parse(args, ["--old", "--new", "--pattern", "--truth"]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var oldExe = new CodeImage(o.Need("--old"));
        var newExe = new CodeImage(o.Need("--new"));
        var r = global::SigRegen.Regenerate(oldExe, newExe, o.Need("--pattern"));
        Log.Info($"[sigregen] {(r.Pattern ?? "(실패)")}  - {r.Detail} ({sw.Elapsed.TotalSeconds:0.0}s)");
        if (o.Get("--truth") is not { } truth || r.Pattern == null) return r.Pattern != null ? 0 : 1;

        static string Where(List<SigPattern.Match> ms) =>
            string.Join(",", ms.Select(m => m.Saves.Count > 0 ? $"{m.Rva:x}->{m.Saves[0]:x}" : $"{m.Rva:x}..{m.End:x}").Order());
        var mine = SigPattern.Parse(r.Pattern).Scan(newExe.Target);
        var want = SigPattern.Parse(truth).Scan(newExe.Target);
        // 패턴 시작점은 정답과 다를 수 있으니(정답이 다른 곳에서 시작) call 대상, 아니면 시작 또는 끝 위치 집합으로 비교
        // (후크는 시작, 메모리 시그니처는 패턴 바로 뒤를 읽으므로 끝이 중요)
        static bool SameSet(IEnumerable<int> x, IEnumerable<int> y) => x.Order().SequenceEqual(y.Order());
        bool same = mine.Count == want.Count &&
            (mine.All(m => m.Saves.Count > 0) && want.All(m => m.Saves.Count > 0)
                ? SameSet(mine.Select(m => m.Saves[0]), want.Select(m => m.Saves[0]))
                : SameSet(mine.Select(m => m.Rva), want.Select(m => m.Rva)) || SameSet(mine.Select(m => m.End), want.Select(m => m.End)));
        Log.Info($"[sigregen] 정답: {Where(want)}  /  복구: {Where(mine)}  => {(same ? "일치" : "불일치")}");
        return same ? 0 : 1;
    }

    // (개발용) 두 exe 의 함수를 명령어 모양 단위로 나란히 비교
    public static int FnDiff(string[] args)
    {
        var o = Options.Parse(args, ["--old", "--new", "--old-rva", "--new-rva"]);
        var oldExe = new CodeImage(o.Need("--old"));
        var newExe = new CodeImage(o.Need("--new"));
        var a = oldExe.FunctionTokens(Convert.ToInt32(o.Need("--old-rva"), 16));
        var b = newExe.FunctionTokens(Convert.ToInt32(o.Need("--new-rva"), 16));
        Log.Info($"old {a.Length} / new {b.Length} instructions");
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string x = i < a.Length ? a[i] : "", y = i < b.Length ? b[i] : "";
            Log.Info($"{(x == y ? "  " : "!!")} {i,4} {x,-40} {y}");
        }
        return 0;
    }
}
