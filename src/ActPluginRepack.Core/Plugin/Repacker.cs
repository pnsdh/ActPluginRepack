using Mono.Cecil;

// opcode / override / data 를 플러그인 DLL에 주입하고, deucalion 시그니처를 교체한다.
// override 와 data 는 같은 resource.dll 안이므로 inner DLL 은 stage 수와 무관하게
// costura 리소스당 한 번만 풀고 다시 압축한다.
//
// 시그니처(input\signatures\deucalion.txt):
//   deucalion dll 의 시그니처 문자열을 같은 길이로 패치해 output 에 쓰고, Machina 의 _checksum 을 새 해시로 맞춘다.
//   Recv 이고 Machina 에 런타임 RECV 시그니처가 있으면(3.0.3.3+) 그 문자열도 같이 바꾼다 — 런타임 쪽은 내장 Recv 가
//   실패했을 때만 쓰이므로 한쪽만 바꾸면 무시될 수 있다.
internal static class Repacker
{
    // 이름 override 파일: 플러그인 로더가 이 접두어로 리소스를 찾는다
    public const string OverrideFilePrefix = "global_";
    public static bool IsOverrideFile(string fileName) => fileName.StartsWith(OverrideFilePrefix, StringComparison.Ordinal);

    private sealed record Injection(string Costura, string ResourceName, string FilePath, bool AllowAdd);

    // stage dir 이 null 이면 그 단계는 생략. 실패는 ToolException.
    // 플러그인 외에 output 폴더에 쓴 파일(패치한 deucalion)의 이름을 돌려준다.
    public static IReadOnlyList<string> Execute(string pluginPath, string outputPath,
                                                string? opcodesDir, string? overridesDir, string? dataDir,
                                                string? signaturesDir)
    {
        pluginPath = Path.GetFullPath(pluginPath);
        outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(pluginPath)) throw new ToolException($"플러그인 파일이 없습니다: {pluginPath}");
        if (pluginPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"원본 플러그인과 출력 경로가 같습니다. 원본은 덮어쓸 수 없습니다: {pluginPath}");
        // Injection 하나 = 파일 하나 -> 안쪽(costura) DLL 속 내장 리소스 하나. (입력 파일 검사가 먼저)
        var plan = new List<Injection>();
        Collect(opcodesDir, Costura.Machina, Costura.OpcodePrefix, overridesOnly: false, allowAdd: false, plan);
        Collect(overridesDir, Costura.Resource, Costura.OverridePrefix, overridesOnly: true, allowAdd: true, plan);
        Collect(dataDir, Costura.Resource, Costura.GeneratedPrefix, overridesOnly: false, allowAdd: false, plan);
        using var plugin = ReadPlugin(pluginPath);
        var sigs = SignatureInput.Load(signaturesDir);
        var memorySigs = SignatureInput.Load(signaturesDir, SignatureInput.MemoryFile);
        var oodleSigs = SignatureInput.Load(signaturesDir, SignatureInput.OodleFile);
        if (plan.Count == 0 && sigs.Count == 0 && memorySigs.Count == 0 && oodleSigs.Count == 0)
            throw new ToolException("넣을 재료가 없습니다. 모든 단계가 비었거나 건너뛰었습니다.");

        Log.Info($"[*] 원본 플러그인: {pluginPath}");
        Log.Info($"[*] 출력: {outputPath}");

        string outDir = Path.GetDirectoryName(outputPath)!;
        var sigPlan = sigs.Count > 0
            ? PlanSignatures(plugin, Path.GetDirectoryName(pluginPath)!, sigs)
            : new SignaturePlan(null, null, null);

        // resource.dll 을 패치할 때 그 안에서 before/after 를 스냅샷해 두고,
        // 패치하지 않았으면 마지막에 한 번만 읽어 "전부 unchanged" 로 보고한다.
        Dictionary<string, byte[]>? baseOverrides = null, finalOverrides = null;

        // 리소스 교체 말고 안쪽 어셈블리에 직접 하는 수정 (costura 이름 -> 수정들)
        var innerPatches = new Dictionary<string, List<Action<ModuleDefinition>>>(StringComparer.Ordinal);
        void AddPatch(string costuraName, Action<ModuleDefinition> patch)
        {
            if (!innerPatches.TryGetValue(costuraName, out var list)) innerPatches[costuraName] = list = [];
            list.Add(patch);
        }
        if (sigPlan.MachinaPatch != null) AddPatch(Costura.Machina, sigPlan.MachinaPatch);
        if (memorySigs.Count > 0) AddPatch(PluginSignatures.MemoryCostura, m => PluginSignatures.PatchMemory(m, memorySigs));
        if (oodleSigs.Count > 0) AddPatch(Costura.Machina, m => PluginSignatures.PatchOodle(m, oodleSigs));

        var costuraNames = plan.Select(i => i.Costura).Concat(innerPatches.Keys).Distinct().ToList();

        foreach (string name in costuraNames)
        {
            var group = plan.Where(i => i.Costura == name).ToList();
            Log.Info($"[*] -> {name}");
            var costura = Costura.Find(plugin, name)
                          ?? throw new ToolException($"리소스가 없습니다: {name}");

            byte[] rebuilt;
            using (var inner = Costura.ReadInner(plugin, name))
            {
                bool isResourceDll = name == Costura.Resource;
                if (isResourceDll) baseOverrides = SnapshotOverrides(inner);

                // override 는 마지막 열거 리포트가 다루므로 파일별 로그를 중복 출력하지 않는다
                if (group.Count > 0)
                    ApplyInjections(inner, group, silencePrefix: isResourceDll ? Costura.OverridePrefix : null);
                foreach (var patch in innerPatches.GetValueOrDefault(name) ?? []) patch(inner);

                if (isResourceDll) finalOverrides = SnapshotOverrides(inner);

                using var outMs = new MemoryStream();
                inner.Write(outMs, new WriterParameters()); // 공개키 토큰은 유지, 서명은 무효인 채로 둔다
                rebuilt = outMs.ToArray();
            }

            byte[] recompressed = Costura.Deflate(rebuilt);
            var attrs = costura.Attributes;
            plugin.Resources.Remove(costura);
            plugin.Resources.Add(new EmbeddedResource(name, attrs, recompressed));
            Log.Info($"    = 다시 만듦 {rebuilt.Length} 바이트, 압축 {recompressed.Length} 바이트");
        }

        if (baseOverrides == null)
        {
            if (Costura.Find(plugin, Costura.Resource) != null)
            {
                using var inner = Costura.ReadInner(plugin, Costura.Resource);
                baseOverrides = finalOverrides = SnapshotOverrides(inner);
            }
        }

        // 최종 플러그인에 들어간 override 를 항상 열거한다(주입이 없었어도).
        // 각 항목을 베이스 플러그인 대비 unchanged / changed / new 로 분류.
        ReportOverrides(baseOverrides ?? [], finalOverrides ?? []);

        // 둘 다 임시 파일에 다 쓴 뒤에 제자리로 옮긴다 — 중간에 실패해도 반쪽짜리(체크섬이 안 맞는 짝)가 남지 않게
        Directory.CreateDirectory(outDir);
        string pluginTmp = outputPath + ".tmp";
        plugin.Write(pluginTmp, new WriterParameters());
        string? deucalionOut = sigPlan.Deucalion != null ? Path.Combine(outDir, sigPlan.DeucalionName!) : null;
        if (deucalionOut != null) File.WriteAllBytes(deucalionOut + ".tmp", sigPlan.Deucalion!);
        File.Move(pluginTmp, outputPath, overwrite: true);
        Log.Info($"[+] 씀: {outputPath}");
        if (deucalionOut == null) return [];
        File.Move(deucalionOut + ".tmp", deucalionOut, overwrite: true);
        Log.Info($"[+] 씀: {deucalionOut}");
        return [sigPlan.DeucalionName!];
    }

    // 플러그인이 아닌 파일이면 알아들을 수 있는 말로
    public static ModuleDefinition ReadPlugin(string path)
    {
        ModuleDefinition module;
        try { module = ModuleDefinition.ReadModule(path); }
        catch (BadImageFormatException) { throw new ToolException($"FFXIV_ACT_Plugin.dll 이 아닙니다 (.NET 어셈블리가 아님): {path}"); }
        if (Costura.Find(module, Costura.Machina) == null)
        {
            module.Dispose();
            throw new ToolException($"FFXIV_ACT_Plugin.dll 이 아닙니다 ({Costura.Machina} 없음): {path}");
        }
        return module;
    }

    // MachinaPatch: Machina 에 적용할 IL 수정(런타임 RECV 문자열 / _checksum). Deucalion: 패치된 dll.
    private sealed record SignaturePlan(Action<ModuleDefinition>? MachinaPatch, byte[]? Deucalion, string? DeucalionName);

    private static SignaturePlan PlanSignatures(ModuleDefinition plugin, string pluginDir, List<Deucalion.Sig> sigs)
    {
        Log.Info($@"[*] 시그니처 (input\signatures\{SignatureInput.FileName})");
        int runtimeCount;
        string? expectedChecksum;
        using (var machina = Costura.ReadInner(plugin, Costura.Machina))
        {
            runtimeCount = DeucalionMachina.RuntimeSignatures(machina).Count;
            expectedChecksum = DeucalionMachina.ReadChecksum(machina);
        }
        if (runtimeCount > 1)
            throw new ToolException($"Machina 에 런타임 시그니처가 {runtimeCount}개 있어 어느 것이 Recv 인지 알 수 없습니다.");

        string? distrib = Deucalion.FindDistrib(pluginDir);
        byte[]? original = distrib != null ? File.ReadAllBytes(distrib) : null;
        byte[]? dll = original;
        string? runtimeRecv = null;

        var forDll = new List<Deucalion.Sig>();
        foreach (var s in sigs)
        {
            if (runtimeCount == 1 && s.Name.Equals("Recv", StringComparison.OrdinalIgnoreCase))
            {
                // Machina 는 deucalion 의 내장 Recv 가 실패했을 때만 이 문자열을 보낸다 — 내장 패턴이 (엉뚱한 곳에라도)
                // 맞으면 무시되므로, deucalion 이 있으면 내장 문자열도 같이 바꿔 어느 쪽이든 이 패턴을 쓰게 한다.
                runtimeRecv = s.Pattern;
                Log.Info($"    ~ Recv -> Machina 런타임 시그니처: {s.Pattern}");
                if (dll != null) forDll.Add(s);
                continue;
            }
            if (dll == null) throw new ToolException($"'{s.Name}' 시그니처를 넣으려면 {pluginDir} 에 deucalion*.distrib.dll 이 있어야 합니다.");
            forDll.Add(s);
        }
        if (forDll.Count > 0)
        {
            var (patched, changed) = Deucalion.PatchAll(original!, forDll);
            foreach (var s in forDll)
                Log.Info(changed.Contains(s.Name, StringComparer.OrdinalIgnoreCase)
                    ? $"    ~ {s.Name} -> deucalion: {s.Pattern}"
                    : $"    = {s.Name}: same as deucalion built-in");
            if (patched != null) dll = patched;
        }

        if (dll == null || ReferenceEquals(dll, original))
            return new SignaturePlan(runtimeRecv == null ? null : m => SetRuntimeRecv(m, runtimeRecv), null, null);

        // 짝이 안 맞는 deucalion 을 패치하고 체크섬을 맞춰 버리면 플러그인이 맞지 않는 deucalion 을 받아들이게 된다
        if (expectedChecksum != DeucalionMachina.FormatChecksum(Deucalion.Sha256(original!)))
            throw new ToolException($"{Path.GetFileName(distrib)} 이(가) 이 플러그인의 deucalion 이 아닙니다 (Machina _checksum 불일치). " +
                                    @"input\plugin 에는 플러그인과 같은 릴리스의 deucalion 만 두세요.");
        byte[] hash = Deucalion.Sha256(dll);
        Log.Info($"    ~ Machina _checksum -> {DeucalionMachina.FormatChecksum(hash)}");
        return new SignaturePlan(m =>
        {
            if (runtimeRecv != null) SetRuntimeRecv(m, runtimeRecv);
            DeucalionMachina.SetChecksum(m, hash);
        }, dll, Path.GetFileName(distrib));
    }

    private static void SetRuntimeRecv(ModuleDefinition machina, string pattern)
    {
        foreach (var ins in DeucalionMachina.RuntimeSignatures(machina)) ins.Operand = pattern;
    }

    // dir 안의 모든 *.txt 를 plan 에 추가한다. dir 이 null 이면 그 단계는 생략. overridesOnly 면 IsOverrideFile 인 것만.
    private static void Collect(string? dir, string costura, string resPrefix, bool overridesOnly,
                                bool allowAdd, List<Injection> plan)
    {
        if (dir == null) return;
        foreach (string p in Directory.GetFiles(dir, "*.txt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(p);
            if (overridesOnly && !IsOverrideFile(fn))
            {
                Log.Warn($"    !! '{fn}' 건너뜀 - 파일 이름이 {OverrideFilePrefix}*.txt 여야 함");
                continue;
            }
            string text = File.ReadAllText(p);
            if (string.IsNullOrWhiteSpace(text)) throw new ToolException($"파일이 비어 있습니다: {p}");
            // opcode 는 형식이 틀리면 Machina 가 통째로 못 읽는다 — 넣기 전에 검사
            if (resPrefix == Costura.OpcodePrefix) MachinaOpcodes.Parse(text, p);
            // override 는 플러그인이 파일마다 ToDictionary 로 읽는다 — 같은 키가 두 줄이면 그 파일과 뒤의 override 가 전부 무시된다
            if (resPrefix == Costura.OverridePrefix &&
                Rsv.Parse(text).GroupBy(x => x.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList() is { Count: > 0 } dup)
                throw new ToolException($"{fn}: 같은 키가 두 번 이상 있습니다 ({string.Join(", ", dup.Take(5))}{(dup.Count > 5 ? $" 외 {dup.Count - 5}개" : "")}). " +
                                        "이대로면 플러그인이 이 파일과 그 뒤의 override 를 모두 무시하므로 한 줄만 남기세요.");
            plan.Add(new Injection(costura, resPrefix + fn, p, allowAdd));
        }
    }

    private static void ApplyInjections(ModuleDefinition inner, IEnumerable<Injection> items, string? silencePrefix)
    {
        var defaultAttrs = inner.Resources.OfType<EmbeddedResource>().FirstOrDefault()?.Attributes
                           ?? ManifestResourceAttributes.Public;
        foreach (var item in items)
        {
            bool quiet = silencePrefix != null && item.ResourceName.StartsWith(silencePrefix, StringComparison.Ordinal);
            // 리소스 이름은 대소문자를 가리지만 Windows 파일 이름은 아니다 (korean.txt -> Korean.txt)
            var existing = Costura.Find(inner, item.ResourceName)
                           ?? inner.Resources.OfType<EmbeddedResource>()
                               .FirstOrDefault(r => r.Name.Equals(item.ResourceName, StringComparison.OrdinalIgnoreCase));
            var inj = existing != null ? item with { ResourceName = existing.Name } : item;
            byte[] newBytes = File.ReadAllBytes(inj.FilePath);
            if (existing != null)
            {
                int old = existing.GetResourceData().Length;
                inner.Resources.Remove(existing);
                inner.Resources.Add(new EmbeddedResource(inj.ResourceName, existing.Attributes, newBytes));
                if (!quiet) Log.Info($"    ~ {inj.ResourceName}: {old} -> {newBytes.Length} 바이트");
            }
            else if (inj.AllowAdd)
            {
                inner.Resources.Add(new EmbeddedResource(inj.ResourceName, defaultAttrs, newBytes));
                if (!quiet) Log.Info($"    + {inj.ResourceName}: 새로 추가, {newBytes.Length} 바이트");
            }
            else
            {
                // 조용히 건너뛰면 빌드는 성공한 것처럼 보이는데 그 데이터는 안 들어간다
                throw new ToolException($"플러그인에 '{inj.ResourceName}' 리소스가 없습니다 ({inj.FilePath}). 파일 이름을 확인하세요 " +
                                        "(새 플러그인 버전에서 이름이 바뀌었을 수도 있음).");
            }
        }
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
        Log.Info($"[*] 플러그인의 이름 override: {names.Count}개");
        foreach (var n in names)
        {
            string status;
            if (!baseOv.TryGetValue(n, out byte[]? ob)) { status = "new"; add++; }
            else if (!ob.SequenceEqual(finalOv[n])) { status = "changed"; chg++; }
            else status = "unchanged";
            Log.Info($"      [{status,-9}] {n[Costura.OverridePrefix.Length..]}");
        }
        Log.Info($"[*] 이름 override: 그대로 {names.Count - chg - add}, 바뀜 {chg}, 추가 {add}");
    }
}
