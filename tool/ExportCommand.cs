using Mono.Cecil;

// 플러그인 DLL에 내장된 data/override/opcode 텍스트 리소스를 파일로 추출한다
// (repack 으로 재주입하기 전 편집 시작점). Cecil 메타데이터 읽기만 하며
// 어셈블리를 프로세스에 로드하지 않는다.
internal static class ExportCommand
{
    public static int Run(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--plugin", "--what", "--out", "--filter"]);
        string root = ProjectRoot.Resolve(o);
        string plugin = o.Get("--plugin", Path.Combine(root, "input", "plugin", "FFXIV_ACT_Plugin.dll"));
        string what = o.Get("--what", "data").ToLowerInvariant();
        string[] filters = o.GetList("--filter");

        // costura 이름 + 리소스 접두사 + 기본 출력 폴더. data 는 생성 산출물(root\data),
        // opcodes/overrides 는 손편집 입력(input\)으로 내보낸다.
        (string costuraName, string prefix, string defaultOut) = what switch
        {
            "opcodes" => (Costura.Machina, Costura.OpcodePrefix, Path.Combine(root, "input", "opcodes")),
            "overrides" => (Costura.Resource, Costura.OverridePrefix, Path.Combine(root, "input", "overrides")),
            "data" => (Costura.Resource, Costura.GeneratedPrefix, Path.Combine(root, "data")),
            _ => throw new ToolException($"--what must be data|overrides|opcodes (got '{what}')"),
        };
        string outDir = o.Get("--out", defaultOut);

        if (!File.Exists(plugin)) throw new ToolException($"plugin not found: {plugin}");
        using var module = ModuleDefinition.ReadModule(plugin);
        var costura = Costura.Find(module, costuraName)
                      ?? throw new ToolException($"resource not found: {costuraName}");
        using var ms = new MemoryStream(Costura.Inflate(costura.GetResourceData()), writable: false);
        using var inner = ModuleDefinition.ReadModule(ms);

        Directory.CreateDirectory(outDir);
        int count = 0;
        foreach (var res in inner.Resources.OfType<EmbeddedResource>())
        {
            if (!res.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (what == "data" &&
                (res.Name.Contains(".Override.", StringComparison.OrdinalIgnoreCase) ||
                 !res.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))) continue;

            string fn = res.Name[prefix.Length..];
            if (filters.Length > 0 && !filters.Any(f => fn.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

            byte[] bytes = res.GetResourceData();
            File.WriteAllBytes(Path.Combine(outDir, fn), bytes);
            Console.WriteLine($"  exported {fn} ({bytes.Length} bytes)");
            count++;
        }
        Console.WriteLine($"[+] {count} file(s) -> {outDir}");
        return 0;
    }
}
