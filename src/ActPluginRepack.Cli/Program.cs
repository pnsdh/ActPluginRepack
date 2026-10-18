// ActPluginRepack — FFXIV 패치 직후, 아직 업데이트되지 않은 FFXIV_ACT_Plugin 을 임시로 고쳐 쓰는 도구의 명령줄 프런트엔드.
// 기능은 전부 ActPluginRepack.Core 에 있고, 여기서는 옵션을 읽어 Core 를 부르고 결과를 콘솔에 보여 준다.
// 명령 목록은 Usage() 참고. Ctrl+C 는 진행 중인 작업을 취소한다.
internal static class Program
{
    // 오류 로그에 값을 남기지 않을 옵션 (github --token <토큰>)
    private static readonly string[] SecretOptions = ["--token"];

    private static int Main(string[] args)
    {
        // 이름만 준 실행 파일(gh, git, py 등)을 현재 폴더(받은 파일이 있을 수 있는 곳)에서 찾지 않게 — PATH 에서만
        Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1");
        Errors.CommandLine = true; // 오류 문장에 명령줄 안내(옵션 이름 등)를 붙인다
        if (args.Length == 0) return Usage();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // 두 번째 Ctrl+C 는 그대로 종료
            e.Cancel = true;
            Console.Error.WriteLine("취소하는 중... (한 번 더 누르면 바로 종료)");
            cts.Cancel();
        };
        using var op = Operation.Begin(ConsoleReporter.Instance, cts.Token);
        try
        {
            string[] rest = args[1..];
            Func<int>? command = args[0] switch
            {
                "status" => () => Commands.Status(rest),
                "detect" => () => Commands.Detect(rest),
                "github" => () => Commands.GitHubToken(rest),
                "prepare" => () => Commands.Prepare(rest),
                "build" => () => Commands.Build(rest),
                "install" => () => Commands.Install(rest),
                "restore" => () => Commands.Restore(rest),
                "hotfix" => () => Commands.Hotfix(rest),
                "rsv" => () => Commands.RsvImport(rest),
                "release-keygen" => () => Commands.ReleaseKeygen(rest),
                "release-sign" => () => Commands.ReleaseSign(rest),
                "sigcheck" => () => Commands.Sigcheck(rest),
                "repack" => () => Commands.Repack(rest),
                "csv" => () => Commands.Csv(rest),
                "export" => () => Commands.Export(rest),
                "fetch-exe" => () => Commands.FetchExe(rest),
                "sigscan" => () => SigCommands.Scan(rest),
                "sigregen" => () => SigCommands.Regen(rest),
                "fndiff" => () => Commands.FnDiff(rest),
                "replay" => () => Commands.Replay(rest),
                "report" => () => Commands.Report(rest),
                "version" or "--version" or "-v" => Version,
                "help" or "-h" or "--help" => () => Usage(),
                _ => null,
            };
            if (command == null) return Usage($"알 수 없는 명령입니다: '{args[0]}'");
            // <명령> --help: 명령 바로 뒤에 줄 때만 (옵션 값으로 준 -h 는 그 옵션의 값 — github --token -h 등)
            if (rest is ["--help" or "-h", ..]) return Usage();
            return LoggedCommands.Contains(args[0]) && TaskLogWorkspace(rest) is { } ws
                ? TaskLog.Run(ws, args[0], $"명령: {string.Join(" ", args)}", command)
                : command();
        }
        catch (Exception ex) when (ex is ToolException or SignatureException) // 시그니처 라이브러리의 입력 오류도 같은 사용자용 오류
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Of(ex); // 관리자 권한 재실행도 같은 코드
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("작업을 취소했습니다.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            // 예상 못 한 예외: 짧은 안내만 보이고 전체 내용은 파일로 (ACTREPACK_DEBUG=1 이면 화면에도)
            Errors.WriteLog(ex, string.Join(" ", args.Select((a, i) => i > 0 && SecretOptions.Contains(args[i - 1]) ? "***" : a)));
            Console.Error.WriteLine($"error: {Errors.Describe(ex)}");
            Console.Error.WriteLine($"       (자세한 내용: {Errors.LogPath})");
            if (Environment.GetEnvironmentVariable("ACTREPACK_DEBUG") == "1") Console.Error.WriteLine(ex);
            return ExitCodes.Error;
        }
    }

    // 작업 기록(작업 폴더의 logs\)을 남기는 명령 — 작업 폴더를 바꾸는 것만 (읽기만 하는 status / sigcheck 등은 남기지 않는다)
    private static readonly string[] LoggedCommands = ["prepare", "build", "install", "restore", "hotfix", "rsv", "repack"];

    // 기록을 남길 작업 폴더 (명령과 같은 규칙: --root, 없으면 현재 폴더에서 위로). 찾지 못하거나 없는 폴더면 null — 명령이 그 오류를 알린다.
    // 관리자 권한으로 다시 실행된 쪽(--log)도 null: 그 출력은 원래 쪽이 받아 원래 쪽 기록에 들어간다.
    private static Workspace? TaskLogWorkspace(string[] rest)
    {
        if (rest.Contains(ElevatedRunner.LogOption)) return null;
        int i = Array.LastIndexOf(rest, "--root");
        try
        {
            var ws = Workspace.Find(i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null);
            return Directory.Exists(ws.Root) ? ws : null;
        }
        catch (Exception ex) when (ex is ToolException or ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static int Version()
    {
        Console.WriteLine($"ActPluginRepack {AppInfo.DisplayVersion}");
        if (AppInfo.CheckForUpdate() is { } update) Console.WriteLine($"새 버전 {update.Tag}: {update.Url}");
        return ExitCodes.Ok;
    }

    // 도움말을 달라고 했으면 stdout, 명령을 잘못 줘서 보여 주는 것이면 stderr
    private static int Usage(string? error = null)
    {
        var output = error == null ? Console.Out : Console.Error;
        if (error != null) Console.Error.WriteLine($"error: {error}\n");
        output.WriteLine($"""
            ActPluginRepack {AppInfo.DisplayVersion}  ({AppInfo.RepoUrl})
            usage: ActPluginRepack <command> [options]   (ActPluginRepack <command> --help 도 이 도움말)
                   --root <dir>: 작업 폴더 (작업 폴더를 쓰는 명령만, 생략 시 현재 폴더에서 위로 찾음)
                   여러 값: install --target 은 옵션을 여러 번 주고, 나머지(a,b / ,... 로 적은 것)는 쉼표로 잇는다

            보통 쓰는 순서
              status   지금 상태와 다음에 할 일 (게임/플러그인 버전, prepare, build, ACT 설치)
                         GitHub API 사용량과 이 도구의 새 버전은 인터넷으로 확인
              detect   설치된 게임 클라이언트 찾기   --save (찾은 것을 config.json 의 clients 로 저장)
              prepare  입력 데이터 준비: 최신 공식 플러그인 -> 스냅샷 -> 시그니처 점검·자동 복구 -> opcode -> CSV
                         끝에 패치가 필요한지 판단한다 (공식 플러그인이 이미 맞으면 불필요)
                         --plugin <dll> --no-plugin --no-snapshot --no-sigcheck --no-repair --no-opcodes --no-csv
                         --ida (IDA 로 직접 추출해 그 결과를 우선)  --overwrite (추출 캐시 무시)
                         opcode: Yarukon > karashiiro(버전 확인 시) > 로컬 opcodediff(지금 exe vs 직전 출시 exe, 항상 대조) > ...
                         --ida-target <지역> (IDA 로 분석할 exe: simplified-chinese(기본, 없으면 받음) / global / korean)
                         설치된 게임이 최신 버전이 아니면 그 클라이언트는 빼고 진행 (모두 오래됐으면 멈춤)
                         --sources a,b (출처 제한: {Wrap(Prepare.AllSourceNames, 66, 28)})
                         exit 2: 깨진 후크 또는 시그니처 점검 실패,  exit 3: 준비 미완료 (opcode 미공개 등)
                         exit 5: 패치 불필요 (공식 플러그인이 이미 맞음 — install --official),  exit 6: 판단할 수 없음
              build    input\ 으로 output\FFXIV_ACT_Plugin.dll 만들기 (CSV 변환 + 주입 + 부속 파일)
                         --plugin <dll> --no-csv
              install  output\ 를 ACT 의 FFXIV 플러그인 폴더에 설치 (원래 파일은 backups\ 에)
                         --target <플러그인 폴더> (여러 번 줄 수 있음. 생략 시 ACT 설정에서 찾음 - 해루봇이면
                                                   한국·글로벌 서버용 폴더 모두, 중국 서버용은 제외)
                         --overlay <OverlayPlugin resources 폴더 | ->
                         extras 의 OverlayPlugin opcodes.jsonc 도 넣는다. ACT 와 게임을 끄고 실행 (켜져 있으면 exit 4).
                         같은 파일이 이미 설치되어 있으면 아무것도 하지 않는다.
                         --official  패치본 대신 input\plugin 의 공식 최신본을 설치 (공식이 이미 게임과 맞을 때,
                                     ACT 에 더 새 버전이 있으면 설치하지 않음)
              restore  마지막 install 하나를 그 직전으로 되돌리기 (ACT/게임이 켜져 있으면 exit 4). 개발·비상용 —
                         화면의 [공식 플러그인으로 되돌리기]는 install --official 과 같다
                         --backup <backups\시각 폴더> (상대 경로는 작업 폴더 기준)
              rsv      ACT 로그의 RSV(OverlayPlugin 262 줄, 새 레이드 스킬 이름 등) -> input\overrides\global_rsv.txt
                         공식 플러그인에 같은 값으로 있는 키는 넣지 않고, 공식이나 input\overrides\global_*.txt 와
                         값이 다르면 알린다 (exit 2)
                         --logs <파일|폴더,...> (기본: ACT 설정의 로그 폴더)  --days <일> (폴더면 최근 며칠, 기본 {Rsv.DefaultDays})
              hotfix   패치본 배포 묶음: hotfix\<패치>\ 에 zip + release-notes.md (FFXIV_ACT_Plugin_HotFix 릴리스용)
                         --tag <패치 번호> (생략 시 게임 버전에서)  올리는 gh 명령을 알려 준다
              github   GitHub API 사용량과 토큰 (토큰 없으면 시간당 60회, 있으면 5000회)
                         --token <토큰 | -> 이 PC 에 저장 (- 면 표준 입력에서 읽음)  --clear 저장한 토큰 지움
                         토큰 출처: 환경 변수 GITHUB_TOKEN > 저장한 토큰 > GitHub CLI(gh) 로그인
              report   문제 보고용 기록 묶음 (zip: 요약과 상태, 작업 기록, 오류 기록, 설정·재료 기록, ACT 플러그인 폴더의
                         파일 목록 — GitHub 토큰, dll·exe 자체, 캐시는 넣지 않음. 폴더 경로가 들어 있어 사용자 이름이 보일 수 있음)
                         --out <zip | 폴더> (생략 시 현재 폴더에 ActPluginRepack-기록-<시각>.zip)
                         prepare / build / install / restore / hotfix / rsv / repack 은 작업 폴더의 logs\ 에
                         작업 기록을 남긴다 (최근 {TaskLog.Keep}개)

            세부 단계
              sigcheck 게임 클라이언트에 시그니처(deucalion 후크 / 메모리 / Oodle)가 맞는지 (깨진 후크 exit 2)
                         --plugin <dll> --game <dir,...> --signatures <dir>
              repack   opcode/override/data/시그니처를 플러그인에 주입
                         --plugin <dll> --out <dll> --opcodes <dir|-> --overrides <dir|-> --data <dir|->
                         --signatures <dir|->   ('-' 는 그 단계 생략; 기본 폴더가 없으면 자동 생략)
                         (--out 의 예전 이름 --output 도 받는다)
              csv      데이터마이닝 CSV -> id|Name 리스트   --csv-dir <dir> --out <dir> --lang <name> --lists Action,...
              export   플러그인의 내장 리소스를 파일로   --what data|overrides|opcodes --plugin <dll> --out <dir>
                         --filter a,b --force (기존 파일 덮어쓰기)

            시그니처·exe 도구 (개발용 — sigscan / sigregen --patterns 는 OverlayPlugin·cactbot 점검 도구도 부른다)
              fetch-exe 공식 패치 서버(Thaliak)에서 특정 버전 exe 받기
                         --region global|korean|simplified-chinese --version <게임 버전 | 패치 번호>
                         (작업 폴더의 snapshots\ 에)  또는 --patch <패치 파일 버전> --out <파일> (--out 은 --patch 와 함께만)
              sigscan  패턴 목록(JSON)을 exe 에서 찾고 가리키는 값까지 (형식: docs\CLI.md)
                         --patterns <json> (--exe <exe> | --region <지역> --version <게임 버전 | 패치 번호>)
                         --all-sections (.text 말고 모든 섹션)  --json (결과를 stdout 에 JSON 으로)
              sigregen 옛 exe 에서 맞던 시그니처를 새 exe 용으로 다시 만들기
                         --old <exe> --new <exe> --pattern <pelite 시그니처> [--truth <정답 시그니처>]
                         --patterns <json> [--json]  여러 개를 한 번에, 읽는 자리(offset)도 다시 계산
                         exe 대신 --region <지역> --old-version <버전> / --new-version <버전> (snapshots\ 보관본, 없으면 받음)
              fndiff   두 exe 의 함수를 명령어 모양 단위로 비교   --old --new --old-rva --new-rva

            개발용 시험
              replay   지난 패치 재현: 그 출시를 공개 opcode 자료가 없는 패치 당일 오전 조건으로 다시 세워 prepare -> build
                         하고, 그 뒤에 나온 공식 플러그인(정답)과 opcode·판단·후크를 비교 (모두 맞으면 0, 다르면 1)
                         --version <게임 버전> (생략 시 최근 출시 목록)  --region <지역> (기본 global)
                         --base <태그> --ref <태그> (생략 시 출시 시각으로)  --sources a,b (기본 localdiff,sigregen)
                         --work <dir> (기본 %TEMP%\actrepack-replay\<지역>-<버전>)  --keep (끝나도 작업 폴더를 남김)
                         --tools <tools 폴더> (받아 둔 python·radare2·opcodediff-venv 를 복사해 씀)

            배포하는 사람용
              release-keygen  자동 업데이트 서명 키 만들기   --out <파일>
              release-sign    배포 zip 에 서명 -> <zip>.sig (publish.ps1 이 부름)   --zip <파일> --key <파일>
                              키 파일 기본값: 환경 변수 ACTREPACK_SIGNING_KEY, 없으면 %USERPROFILE%\.actrepack\release-signing-key.pem

            종료 코드: 0 정상, 1 실패, 130 취소 (Ctrl+C)
                       2 prepare / sigcheck 깨진 후크 (prepare 는 시그니처 점검 실패도), rsv 값이 다른 키,
                         sigscan 0곳에 맞는 패턴, sigregen --patterns 만들지 못한 패턴 (패턴 하나일 때는 1)
                       3 prepare 준비 미완료, sigscan 여러 곳에 맞는 패턴,  4 install / restore 때 ACT 나 게임이 켜져 있음
                       5 prepare 패치 불필요 (공식이 이미 맞음),  6 prepare 패치가 필요한지 판단할 수 없음
                       7 install 때 ACT 에 더 새 플러그인이 있어 그대로 둠
                       sigscan 은 값을 해석하지 못한 것이 있으면 1 (--json 결과는 그래도 나옴)
            """);
        return error == null ? ExitCodes.Ok : ExitCodes.Error;
    }

    // 이름 목록을 쉼표로 이어 한 줄이 width 자를 넘지 않게 나눈다 (이어지는 줄은 indent 칸 들여 씀)
    private static string Wrap(IEnumerable<string> names, int width, int indent)
    {
        var lines = new List<string>();
        string current = "";
        foreach (string name in names)
        {
            string next = current.Length == 0 ? name : $"{current}, {name}";
            if (current.Length > 0 && next.Length > width) { lines.Add(current + ","); current = name; }
            else current = next;
        }
        lines.Add(current);
        return string.Join(Environment.NewLine + new string(' ', indent), lines);
    }
}
