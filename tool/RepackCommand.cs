using Mono.Cecil;

// opcode / override / data 를 플러그인 DLL에 주입한다.
// override 와 data 는 같은 resource.dll 안이므로 inner DLL 은 stage 수와 무관하게
// costura 리소스당 한 번만 풀고 다시 압축한다.
internal static class RepackCommand
{
    private sealed record Injection(string Costura, string ResourceName, string FilePath, bool AllowAdd);

    public static int Run(string[] args)
    {
        var o = Options.Parse(args,
            ["--root", "--plugin", "--output", "--opcodes", "--overrides", "--data"]);
        string root = ProjectRoot.Resolve(o);
        Execute(
            pluginPath: o.Get("--plugin", Path.Combine(root, "input", "plugin", "FFXIV_ACT_Plugin.dll")),
            outputPath: o.Get("--output", Path.Combine(root, "output", "FFXIV_ACT_Plugin.dll")),
            opcodesDir: ResolveStageDir(o, "--opcodes", Path.Combine(root, "input", "opcodes")),
            overridesDir: ResolveStageDir(o, "--overrides", Path.Combine(root, "input", "overrides")),
            dataDir: ResolveStageDir(o, "--data", Path.Combine(root, "data")));
        return 0;
    }

    // '-' = 명시적 생략. 명시된 폴더가 없으면 에러, 기본 폴더가 없으면 자동 생략(null).
    private static string? ResolveStageDir(Options o, string opt, string fallback)
    {
        string? dir = o.Get(opt);
        if (dir == "-") return null;
        if (dir != null)
        {
            if (!Directory.Exists(dir)) throw new ToolException($"dir not found: {dir}");
            return Path.GetFullPath(dir);
        }
        return Directory.Exists(fallback) ? Path.GetFullPath(fallback) : null;
    }

    // stage dir 이 null 이면 그 단계는 생략. 실패는 ToolException.
    public static void Execute(string pluginPath, string outputPath,
                               string? opcodesDir, string? overridesDir, string? dataDir)
    {
        pluginPath = Path.GetFullPath(pluginPath);
        outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(pluginPath)) throw new ToolException($"plugin not found: {pluginPath}");

        // Injection 하나 = 파일 하나 -> 안쪽(costura) DLL 속 내장 리소스 하나.
        var plan = new List<Injection>();
        Collect(opcodesDir, Costura.Machina, Costura.OpcodePrefix, requireName: null, allowAdd: false, plan);
        Collect(overridesDir, Costura.Resource, Costura.OverridePrefix, requireName: "global_", allowAdd: true, plan);
        Collect(dataDir, Costura.Resource, Costura.GeneratedPrefix, requireName: null, allowAdd: false, plan);
        if (plan.Count == 0) throw new ToolException("nothing to do (all stages empty/skipped)");

        Console.WriteLine($"[*] plugin : {pluginPath}");
        Console.WriteLine($"[*] output : {outputPath}");

        using var plugin = ModuleDefinition.ReadModule(pluginPath);

        // resource.dll 을 패치할 때 그 안에서 before/after 를 스냅샷해 두고,
        // 패치하지 않았으면 마지막에 한 번만 읽어 "전부 unchanged" 로 보고한다.
        Dictionary<string, byte[]>? baseOverrides = null, finalOverrides = null;

        foreach (var group in plan.GroupBy(i => i.Costura))
        {
            Console.WriteLine($"[*] -> {group.Key}");
            var costura = Costura.Find(plugin, group.Key)
                          ?? throw new ToolException($"resource not found: {group.Key}");

            byte[] innerBytes = Costura.Inflate(costura.GetResourceData());
            byte[] rebuilt;
            using (var ms = new MemoryStream(innerBytes, writable: false))
            using (var inner = ModuleDefinition.ReadModule(ms))
            {
                bool isResourceDll = group.Key == Costura.Resource;
                if (isResourceDll) baseOverrides = SnapshotOverrides(inner);

                // override 는 마지막 열거 리포트가 다루므로 파일별 로그를 중복 출력하지 않는다
                ApplyInjections(inner, group, silencePrefix: isResourceDll ? Costura.OverridePrefix : null);

                if (isResourceDll) finalOverrides = SnapshotOverrides(inner);

                using var outMs = new MemoryStream();
                inner.Write(outMs, new WriterParameters()); // 공개키 토큰은 유지, 서명은 무효인 채로 둔다
                rebuilt = outMs.ToArray();
            }

            byte[] recompressed = Costura.Deflate(rebuilt);
            var attrs = costura.Attributes;
            plugin.Resources.Remove(costura);
            plugin.Resources.Add(new EmbeddedResource(group.Key, attrs, recompressed));
            Console.WriteLine($"    = rebuilt {rebuilt.Length} bytes, recompressed {recompressed.Length} bytes");
        }

        if (baseOverrides == null)
        {
            var costura = Costura.Find(plugin, Costura.Resource);
            if (costura != null)
            {
                using var ms = new MemoryStream(Costura.Inflate(costura.GetResourceData()), writable: false);
                using var inner = ModuleDefinition.ReadModule(ms);
                baseOverrides = finalOverrides = SnapshotOverrides(inner);
            }
        }

        // 최종 플러그인에 들어간 override 를 항상 열거한다(주입이 없었어도).
        // 각 항목을 베이스 플러그인 대비 unchanged / changed / new 로 분류.
        ReportOverrides(baseOverrides ?? [], finalOverrides ?? []);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        plugin.Write(outputPath, new WriterParameters());
        Console.WriteLine($"[+] wrote {outputPath}");
    }

    // dir 안의 모든 *.txt 를 plan 에 추가한다. dir 이 null 이면 그 단계는 생략.
    private static void Collect(string? dir, string costura, string resPrefix, string? requireName,
                                bool allowAdd, List<Injection> plan)
    {
        if (dir == null) return;
        foreach (string p in Directory.GetFiles(dir, "*.txt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(p);
            if (requireName != null && !fn.StartsWith(requireName, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"    !! '{fn}' skipped: must be named {requireName}*.txt");
                continue;
            }
            plan.Add(new Injection(costura, resPrefix + fn, p, allowAdd));
        }
    }

    private static void ApplyInjections(ModuleDefinition inner, IEnumerable<Injection> items, string? silencePrefix)
    {
        var defaultAttrs = inner.Resources.OfType<EmbeddedResource>().FirstOrDefault()?.Attributes
                           ?? ManifestResourceAttributes.Public;
        int changed = 0;
        foreach (var inj in items)
        {
            bool quiet = silencePrefix != null && inj.ResourceName.StartsWith(silencePrefix, StringComparison.Ordinal);
            var existing = Costura.Find(inner, inj.ResourceName);
            byte[] newBytes = File.ReadAllBytes(inj.FilePath);
            if (existing != null)
            {
                int old = existing.GetResourceData().Length;
                inner.Resources.Remove(existing);
                inner.Resources.Add(new EmbeddedResource(inj.ResourceName, existing.Attributes, newBytes));
                if (!quiet) Console.WriteLine($"    ~ {inj.ResourceName}: {old} -> {newBytes.Length} bytes");
                changed++;
            }
            else if (inj.AllowAdd)
            {
                inner.Resources.Add(new EmbeddedResource(inj.ResourceName, defaultAttrs, newBytes));
                if (!quiet) Console.WriteLine($"    + {inj.ResourceName}: new, {newBytes.Length} bytes");
                changed++;
            }
            else
            {
                Console.Error.WriteLine($"    !! no resource '{inj.ResourceName}' (adding disabled) - skipped");
            }
        }
        if (changed == 0) throw new ToolException("nothing changed in this stage");
    }

    private static Dictionary<string, byte[]> SnapshotOverrides(ModuleDefinition inner) =>
        inner.Resources.OfType<EmbeddedResource>()
            .Where(r => r.Name.StartsWith(Costura.OverridePrefix, StringComparison.Ordinal))
            .ToDictionary(r => r.Name, r => r.GetResourceData(), StringComparer.Ordinal);

    // 최종 플러그인의 override 전체를 베이스 플러그인 대비 분류해 출력한다.
    private static void ReportOverrides(Dictionary<string, byte[]> baseOv, Dictionary<string, byte[]> finalOv)
    {
        if (finalOv.Count == 0) return;
        var names = finalOv.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        int chg = 0, add = 0;
        Console.WriteLine($"[*] overrides in plugin: {names.Count}");
        foreach (var n in names)
        {
            string status;
            if (!baseOv.TryGetValue(n, out byte[]? ob)) { status = "new"; add++; }
            else if (!ob.SequenceEqual(finalOv[n])) { status = "changed"; chg++; }
            else status = "unchanged";
            Console.WriteLine($"      [{status,-9}] {n[Costura.OverridePrefix.Length..]}");
        }
        Console.WriteLine($"[*] overrides: unchanged {names.Count - chg - add}, changed {chg}, new {add}");
    }
}
