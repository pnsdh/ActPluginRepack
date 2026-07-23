// ActPluginRepack — FFXIV_ACT_Plugin.dll 리소스 패치 통합 CLI
//
// 소스 재빌드 없이, Costura.Fody 로 이중 내장된 리소스만 교체해 플러그인을 패치한다:
//
//   FFXIV_ACT_Plugin.dll
//     -> costura.machina.ffxiv.dll.compressed              (Machina.FFXIV.dll 의 DEFLATE)
//          -> Machina.FFXIV.Headers.Opcodes.<Region>.txt              (opcode)
//     -> costura.ffxiv_act_plugin.resource.dll.compressed  (FFXIV_ACT_Plugin.Resource.dll 의 DEFLATE)
//          -> FFXIV_ACT_Plugin.Resource.Generated.Override.global_*.txt (스킬명 오버라이드)
//          -> FFXIV_ACT_Plugin.Resource.Generated.<List>_<Lang>.txt     (게임 데이터 테이블)
//
// 커맨드:
//   build   csv 변환 + repack + 부속 파일 복사 (보통 이것만; 루트의 build.ps1 이 호출)
//   repack  opcode/override/data 를 플러그인에 주입
//   csv     데이터마이닝 CSV -> "id|Name" 리스트 변환
//   export  플러그인에 박힌 리소스를 파일로 추출 (편집 시작점)
//
// 모든 경로 기본값은 프로젝트 루트 기준이다 (--root; 생략 시 현재 폴더에서 위로
// 올라가며 build.ps1 이 있는 폴더를 자동 탐지).

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            string[] rest = args[1..];
            return args[0] switch
            {
                "build" => BuildCommand.Run(rest),
                "repack" => RepackCommand.Run(rest),
                "csv" => CsvCommand.Run(rest),
                "export" => ExportCommand.Run(rest),
                "help" or "-h" or "--help" => Usage(),
                _ => Usage($"unknown command '{args[0]}'"),
            };
        }
        catch (ToolException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Usage(string? error = null)
    {
        if (error != null) Console.Error.WriteLine($"error: {error}\n");
        Console.Error.WriteLine("""
            usage: ActPluginRepack <command> [options]

            commands:
              build    csv 변환 + repack + 부속 파일 복사 (한 번에 전부)
                         --root <dir> --plugin <dll> --no-csv
              repack   opcode/override/data 를 플러그인에 주입
                         --root <dir> --plugin <dll> --output <dll>
                         --opcodes <dir|-> --overrides <dir|-> --data <dir|->
                         ('-' 는 해당 단계 생략; 기본 폴더가 없으면 자동 생략)
              csv      데이터마이닝 CSV -> id|Name 리스트 변환
                         --root <dir> --csv-dir <dir> --out <dir> --lang <name>
                         --lists Action,Status,...
              export   플러그인의 내장 리소스를 파일로 추출
                         --root <dir> --plugin <dll> --what data|overrides|opcodes
                         --out <dir> --filter a,b

            --root 생략 시 현재 폴더에서 위로 올라가며 build.ps1 이 있는 폴더를 찾는다.
            """);
        return error == null ? 0 : 1;
    }
}
