// 한 번에 임시 플러그인 만들기: input\ 의 재료로 output\FFXIV_ACT_Plugin.dll 생성.
//   1) input\csv\<lang> 의 데이터마이닝 CSV -> data\*.txt (언어별 자동 변환)
//   2) input\opcodes + input\overrides + data 를 원본 플러그인에 주입 -> output\
//   3) input\plugin 의 부속 파일(베이스 dll 제외)을 output\ 에 복사
//      -> output 폴더를 ACT 플러그인 폴더에 통째로 복사하면 되게.
//
// data\ 는 항상 CSV에서 생성되는 산출물이므로 손으로 건드리지 않는다
// (직접 입력은 input\ 의 csv/opcodes/overrides 로만).
internal static class BuildCommand
{
    public static int Run(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin"], ["--no-csv"]);
        string root = ProjectRoot.Resolve(o);
        string dataDir = Path.Combine(root, "data");

        // 1) CSV -> data
        //   - 다국어 리스트(Action/Status/Territory/ContentFinder): 모든 언어 폴더마다 생성.
        //   - 고정 3종(ActionCategory/World/Map, 플러그인이 _English 만 읽음): input\csv\en 이
        //     있을 때만 en 에서 생성(영어->영어). en 이 없으면 베이스 DLL 영어값을 그대로 둔다.
        if (!o.Flag("--no-csv"))
        {
            string csvRoot = Path.Combine(root, "input", "csv");
            var langDirs = Directory.Exists(csvRoot)
                ? Directory.GetDirectories(csvRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (langDirs.Count == 0)
            {
                Ui.Note(@"[csv] input\csv 에 언어 폴더 없음 - 데이터 생성 건너뜀");
            }
            else
            {
                // data 를 매번 CSV 에서 새로 생성(오래된 파일 잔류 방지). 변환 실패는 곧바로
                // 빌드 실패다 — 계속 진행하면 누락분이 조용히 베이스 DLL 값으로 남기 때문.
                if (Directory.Exists(dataDir))
                    foreach (string f in Directory.GetFiles(dataDir, "*.txt")) File.Delete(f);
                foreach (string dir in langDirs)
                {
                    string leaf = Path.GetFileName(dir);
                    bool pinned = leaf.Equals("en", StringComparison.OrdinalIgnoreCase);
                    string[] lists = pinned
                        ? [.. CsvCommand.DefaultLists, .. CsvCommand.PinnedLists]
                        : CsvCommand.DefaultLists;
                    Ui.Section($"[csv] {leaf} ->{(pinned ? " (+고정 3종)" : "")}");
                    CsvCommand.Convert(dir, dataDir, lang: null, lists);
                }
                if (!langDirs.Any(d => Path.GetFileName(d).Equals("en", StringComparison.OrdinalIgnoreCase)))
                    Ui.Note("[csv] en 폴더 없음 - 고정 3종(ActionCategory/World/Map)은 베이스 DLL 유지");
            }
        }

        // 2) repack -> output (폴더가 없는 단계는 자동 생략)
        Ui.Section("[repack]");
        string outDir = Path.Combine(root, "output");
        string pluginDir = Path.Combine(root, "input", "plugin");
        RepackCommand.Execute(
            pluginPath: o.Get("--plugin", Path.Combine(pluginDir, "FFXIV_ACT_Plugin.dll")),
            outputPath: Path.Combine(outDir, "FFXIV_ACT_Plugin.dll"),
            opcodesDir: ExistingOrNull(Path.Combine(root, "input", "opcodes")),
            overridesDir: ExistingOrNull(Path.Combine(root, "input", "overrides")),
            dataDir: ExistingOrNull(dataDir));

        // 3) input\plugin 의 부속 DLL(베이스 플러그인 제외)을 output 에 복사
        foreach (string f in Directory.GetFiles(pluginDir, "*.dll"))
        {
            string name = Path.GetFileName(f);
            if (name.Equals("FFXIV_ACT_Plugin.dll", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(f, Path.Combine(outDir, name), overwrite: true);
            Ui.Section($@"[copy] {name} -> output\");
        }

        Ui.Done($"[done] -> {outDir}");
        return 0;
    }

    private static string? ExistingOrNull(string dir) => Directory.Exists(dir) ? dir : null;
}
