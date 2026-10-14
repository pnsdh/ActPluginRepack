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
            return args[0] switch
            {
                "status" => Commands.Status(rest),
                "detect" => Commands.Detect(rest),
                "github" => Commands.GitHubToken(rest),
                "prepare" => Commands.Prepare(rest),
                "build" => Commands.Build(rest),
                "install" => Commands.Install(rest),
                "restore" => Commands.Restore(rest),
                "hotfix" => Commands.Hotfix(rest),
                "rsv" => Commands.RsvImport(rest),
                "release-keygen" => Commands.ReleaseKeygen(rest),
                "release-sign" => Commands.ReleaseSign(rest),
                "sigcheck" => Commands.Sigcheck(rest),
                "repack" => Commands.Repack(rest),
                "csv" => Commands.Csv(rest),
                "export" => Commands.Export(rest),
                "fetch-exe" => Commands.FetchExe(rest),
                "sigregen" => Commands.SigRegen(rest),
                "fndiff" => Commands.FnDiff(rest),
                "version" or "--version" or "-v" => Version(),
                "help" or "-h" or "--help" => Usage(),
                _ => Usage($"알 수 없는 명령입니다: '{args[0]}'"),
            };
        }
        catch (ToolException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex is AppsRunningException ? 4 : 1; // 4: ACT/게임 실행 중 (관리자 권한 재실행도 같은 코드)
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("작업을 취소했습니다.");
            return 130;
        }
        catch (Exception ex)
        {
            // 예상 못 한 예외: 짧은 안내만 보이고 전체 내용은 파일로 (ACTREPACK_DEBUG=1 이면 화면에도)
            Errors.WriteLog(ex, string.Join(" ", args.Select((a, i) => i > 0 && SecretOptions.Contains(args[i - 1]) ? "***" : a)));
            Console.Error.WriteLine($"error: {Errors.Describe(ex)}");
            Console.Error.WriteLine($"       (자세한 내용: {Errors.LogPath})");
            if (Environment.GetEnvironmentVariable("ACTREPACK_DEBUG") == "1") Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Version()
    {
        Console.WriteLine($"ActPluginRepack {AppInfo.DisplayVersion}");
        if (AppInfo.CheckForUpdate() is { } update) Console.WriteLine($"새 버전 {update.Tag}: {update.Url}");
        return 0;
    }

    private static int Usage(string? error = null)
    {
        if (error != null) Console.Error.WriteLine($"error: {error}\n");
        Console.Error.WriteLine($"""
            ActPluginRepack {AppInfo.DisplayVersion}  ({AppInfo.RepoUrl})
            usage: ActPluginRepack <command> [options]
                   --root <dir>: 작업 폴더 (작업 폴더를 쓰는 명령만, 생략 시 현재 폴더에서 위로 찾음)

            보통 쓰는 순서
              status   지금 상태와 다음에 할 일 (게임/플러그인 버전, prepare, build, ACT 설치)
                         GitHub API 사용량과 이 도구의 새 버전은 인터넷으로 확인
              detect   설치된 게임 클라이언트 찾기   --save (찾은 것을 config.json 의 clients 로 저장)
              prepare  입력 데이터 준비: 최신 공식 플러그인 -> 스냅샷 -> 시그니처 점검·자동 복구 -> opcode -> CSV
                         끝에 패치가 필요한지 판단한다 (공식 플러그인이 이미 맞으면 불필요)
                         --plugin <dll> --no-plugin --no-snapshot --no-sigcheck --no-repair --no-opcodes --no-csv
                         --ida / --localdiff (공개본이 있어도 직접 추출해 대조)  --overwrite (추출 캐시 무시)
                         --ida-target <지역> (IDA 로 분석할 exe: simplified-chinese(기본, 없으면 받음) / global / korean)
                         설치된 게임이 최신 버전이 아니면 그 클라이언트는 빼고 진행 (모두 오래됐으면 멈춤)
                         --sources a,b (출처 제한: oxidizer, sharpvision, datamining, xivdata,
                                        yarukon, opcodediff, localdiff, ida, karashiiro, upstream, sigregen)
                         exit 2: 깨진 후크 또는 시그니처 점검 실패,  exit 3: 준비 미완료 (opcode 미공개 등)
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
              restore  마지막 install 전으로 되돌리기 (ACT/게임이 켜져 있으면 exit 4)
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

            세부 단계
              sigcheck 게임 클라이언트에 시그니처(deucalion 후크 / 메모리 / Oodle)가 맞는지 (깨진 후크 exit 2)
                         --plugin <dll> --game <dir,...> --signatures <dir>
              repack   opcode/override/data/시그니처를 플러그인에 주입
                         --plugin <dll> --output <dll> --opcodes <dir|-> --overrides <dir|-> --data <dir|->
                         --signatures <dir|->   ('-' 는 그 단계 생략; 기본 폴더가 없으면 자동 생략)
              csv      데이터마이닝 CSV -> id|Name 리스트   --csv-dir <dir> --out <dir> --lang <name> --lists Action,...
              export   플러그인의 내장 리소스를 파일로   --what data|overrides|opcodes --plugin <dll> --out <dir>
                         --filter a,b --force (기존 파일 덮어쓰기)

            개발용
              fetch-exe 공식 패치 서버(Thaliak)에서 특정 버전 exe 받기
                         --region global|korean|simplified-chinese --version <게임 버전 | 패치 번호>
                         (작업 폴더의 snapshots\ 에)  또는 --patch <패치 파일 버전> --out <파일> (--out 은 --patch 와 함께만)
              sigregen 옛 exe 에서 맞던 시그니처를 새 exe 용으로 다시 만들기
                         --old <exe> --new <exe> --pattern <시그니처> [--truth <정답 시그니처>]
                         (만들지 못했거나 --truth 와 다른 곳을 가리키면 exit 1)
              fndiff   두 exe 의 함수를 명령어 모양 단위로 비교   --old --new --old-rva --new-rva

            배포하는 사람용
              release-keygen  자동 업데이트 서명 키 만들기   --out <파일>
              release-sign    배포 zip 에 서명 -> <zip>.sig (publish.ps1 이 부름)   --zip <파일> --key <파일>
                              키 파일 기본값: 환경 변수 ACTREPACK_SIGNING_KEY, 없으면 %USERPROFILE%\.actrepack\release-signing-key.pem

            종료 코드: 0 정상, 1 실패, 130 취소 (Ctrl+C)
                       2 prepare / sigcheck 깨진 후크 (prepare 는 시그니처 점검 실패도), rsv 값이 다른 키
                       3 prepare 준비 미완료,  4 install / restore 때 ACT 나 게임이 켜져 있음
            """);
        return error == null ? 0 : 1;
    }
}
