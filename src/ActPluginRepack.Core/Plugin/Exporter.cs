using Mono.Cecil;

// 플러그인 DLL에 내장된 data/override/opcode 텍스트 리소스를 파일로 추출한다
// (repack 으로 재주입하기 전 편집 시작점). Cecil 메타데이터 읽기만 하며
// 어셈블리를 프로세스에 로드하지 않는다.
internal static class Exporter
{
    // what: data | overrides | opcodes. outDir 생략 시 기본 위치(data\ 또는 input\opcodes / input\overrides).
    // 이미 있는 파일은 force 가 아니면 덮어쓰지 않는다. 내보낸 파일 수를 돌려준다.
    public static int Export(Workspace ws, string what, string? plugin, string? outDir, string[] filters, bool force)
    {
        plugin ??= ws.PluginPath;
        what = what.ToLowerInvariant();

        // costura 이름 + 리소스 접두사 + 기본 출력 폴더. data 는 생성 산출물(data\),
        // opcodes/overrides 는 손편집 입력(input\)으로 내보낸다.
        (string costuraName, string prefix, string defaultOut) = what switch
        {
            "opcodes" => (Costura.Machina, Costura.OpcodePrefix, ws.OpcodesDir),
            "overrides" => (Costura.Resource, Costura.OverridePrefix, ws.OverridesDir),
            "data" => (Costura.Resource, Costura.GeneratedPrefix, ws.DataDir),
            _ => throw new ToolException($"--what 에는 data, overrides, opcodes 중 하나를 쓰세요 (받은 값: '{what}')."),
        };
        outDir ??= defaultOut;

        if (!File.Exists(plugin)) throw new ToolException($"플러그인 파일이 없습니다: {plugin}");
        using var module = Repacker.ReadPlugin(plugin);
        using var inner = Costura.ReadInner(module, costuraName);

        var files = new List<(string Name, byte[] Bytes)>();
        foreach (var res in inner.Resources.OfType<EmbeddedResource>())
        {
            if (!res.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (what == "data" &&
                (res.Name.StartsWith(Costura.OverridePrefix, StringComparison.Ordinal) ||
                 !res.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))) continue;

            string fn = res.Name[prefix.Length..];
            if (filters.Length > 0 && !filters.Any(f => fn.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
            files.Add((fn, res.GetResourceData()));
        }

        // 손으로 고친 입력(input\opcodes, input\overrides)을 말없이 덮어쓰지 않는다
        var existing = files.Where(f => File.Exists(Path.Combine(outDir, f.Name))).Select(f => f.Name).ToList();
        if (existing.Count > 0 && !force)
            throw new ToolException($"{outDir} 에 이미 파일이 {existing.Count}개 있습니다 ({string.Join(", ", existing.Take(5))}" +
                                    $"{(existing.Count > 5 ? ", ..." : "")}). 덮어쓰려면 --force 를 쓰고, 아니면 --out 으로 다른 폴더를 지정하세요.");

        Directory.CreateDirectory(outDir);
        int count = 0;
        foreach (var (fn, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(outDir, fn), bytes);
            Log.Info($"  내보냄 {fn} ({bytes.Length} 바이트)");
            count++;
        }
        Log.Info($"[+] {count}개 파일 -> {outDir}");
        return count;
    }
}
