using System.IO.Compression;
using System.Text;

// 패치본 배포 묶음 (CLI hotfix): build 결과를 pnsdh/FFXIV_ACT_Plugin_HotFix 에 올릴 zip + 릴리스 설명으로.
//
//   hotfix\<패치>\FFXIV_ACT_Plugin_<패치>_custom_fix.zip
//       FFXIV_ACT_Plugin.dll, deucalion-*.dll (output\), OverlayPlugin\opcodes.jsonc (extras\, 있으면)
//   hotfix\<패치>\release-notes.md   게임 버전 / 공식 플러그인 / 바꾼 것을 input\.state.json · output\.build.json ·
//                                    input\signatures 에서 채운다. "알려진 문제" 만 손으로 고친다.
//
// 패치 번호는 GameVersions 대응표(글로벌 기준)로 클라이언트 버전에서 구한다 — 모르면(대응표 미갱신) tag 로 직접 준다.
// 재료가 지금 게임과 다르거나(오래됨) 빌드가 prepare 보다 먼저면 멈춘다: 틀린 패치본을 올리지 않게.
internal sealed record HotfixResult(string Tag, string ZipPath, string NotesPath, IReadOnlyList<string> Files, IReadOnlyList<string> Warnings)
{
    // 올리는 명령 (올리는 건 사람이 직접 — release-notes.md 의 "알려진 문제" 를 확인한 뒤)
    public string UploadCommand =>
        $"gh release create {Tag} \"{ZipPath}\" --repo {HotfixPackage.Repo} --title {Tag} --notes-file \"{NotesPath}\"";
}

internal static class HotfixPackage
{
    public const string Repo = "pnsdh/FFXIV_ACT_Plugin_HotFix";

    public static HotfixResult Create(Workspace ws, string? tag = null)
    {
        var config = Config.Load(ws.Root);
        var built = BuildInfo.Load(ws.OutputDir) ?? throw new ToolException(@"output\ 에 빌드 기록이 없습니다. 먼저 prepare 와 build 를 실행하세요.");
        var prepared = MaterialState.Load(ws) ?? throw new ToolException("prepare 기록이 없습니다. 먼저 prepare 와 build 를 실행하세요.");
        string plugin = ws.OutputPluginPath;
        if (!File.Exists(plugin)) throw new ToolException(@"output\FFXIV_ACT_Plugin.dll 파일이 없습니다. 먼저 build 를 실행하세요.");

        // 틀린 패치본을 올리지 않게: 재료가 지금 게임 기준인지, 빌드가 그 재료로 만든 것인지
        var stale = prepared.Differences(ws.PluginPath, config.Clients);
        if (stale.Count > 0) throw new ToolException($"{string.Join(" ", stale)} prepare 와 build 를 다시 실행하세요.");
        // (build --plugin <다른 dll> 로 만든 출력이면 prepare 한 베이스와 다르다)
        if (!built.IsCurrentFor(prepared))
            throw new ToolException(built.BuiltAt < prepared.PreparedAt
                ? "빌드가 마지막 prepare 보다 먼저 만들어졌습니다. build 를 다시 실행하세요."
                : $@"output\ 의 플러그인은 input\plugin 의 베이스({prepared.PluginVersion ?? "없음"})가 아니라 " +
                  $"{built.PluginVersion ?? "알 수 없는 것"}으로 만들었습니다. build 를 다시 실행하세요.");
        if (prepared.Outcome is PrepareOutcome.HooksBroken or PrepareOutcome.OpcodesMissing)
            throw new ToolException("마지막 prepare 가 끝나지 않았습니다 (후크가 깨졌거나 opcode 를 다 구하지 못함). 그 상태의 패치본은 올리지 않습니다.");
        var warnings = new List<string>();
        if (prepared.PatchNeeded == false) warnings.Add("prepare 의 판단으로는 공식 플러그인이 이미 맞습니다. 올릴 필요가 없을 수 있습니다.");
        if (prepared.PatchNeeded == null) warnings.Add("prepare 에서 패치가 필요한지 판단하지 못했습니다. 확인한 뒤 올리세요.");
        // 배포 전 점검: 교차 확인을 하지 않았거나 다른 것이 있었으면 알린다 (만들기는 한다 — 사람이 보고 판단)
        if (prepared.CrossCheck == null)
            warnings.Add("교차 확인 없이 준비한 재료입니다. 올리기 전에 " +
                         Errors.CliOr("교차 확인(prepare --cross-check)을 권합니다.", "고급 모드의 '가능한 모든 수단으로 교차 확인'을 권합니다."));
        else
        {
            if (prepared.CrossCheck.Count > 0)
                warnings.Add($"교차 확인에서 출처·수단끼리 다른 것이 있었습니다: {string.Join(", ", prepared.CrossCheck)}. {SourcesHint}");
            if (prepared.CrossCheckGaps is { Count: > 0 } gaps)
                warnings.Add($"교차 확인에서 대조하지 못한 것이 있습니다: {string.Join(", ", gaps)}. {SourcesHint}");
        }
        if (config.Clients.Where(prepared.IsOutdated).Select(c => c.Name).ToList() is { Count: > 0 } skipped)
            warnings.Add($"{string.Join(", ", skipped)}: 게임이 최신 버전이 아니라 prepare 에서 빠졌습니다. 그 지역은 시그니처를 확인하지 못했습니다.");

        tag ??= PatchTag(prepared.GameVersions)
                ?? throw new ToolException("게임 버전에 맞는 패치 번호를 알 수 없습니다 (대응표가 아직 갱신되지 않음). 태그를 직접 지정하세요 (예: --tag 7.58).");
        if (TagProblem(tag) is { } problem) throw new ToolException(problem);

        // 담을 파일
        var entries = new List<(string Source, string Name)> { (plugin, PluginRelease.PluginFile) };
        entries.AddRange(Directory.GetFiles(ws.OutputDir, "deucalion*.dll").Select(f => (f, Path.GetFileName(f))));
        string overlay = ws.OverlayOpcodesPath;
        if (File.Exists(overlay)) entries.Add((overlay, $"OverlayPlugin/{Install.OverlayOpcodes}"));

        string dir = ws.HotfixDir(tag);
        Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, $"FFXIV_ACT_Plugin_{tag}_custom_fix.zip");
        string tmp = zip + ".tmp";
        File.Delete(tmp);
        using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
            foreach (var (source, name) in entries)
                archive.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
        File.Move(tmp, zip, overwrite: true);

        string notes = Path.Combine(dir, "release-notes.md");
        Files.WriteAtomic(notes, Notes(ws, tag, prepared, built, entries.Select(e => e.Name).ToList()));
        Log.Done($"[hotfix] {zip}");
        Log.Done($"[hotfix] {notes}");
        foreach (string w in warnings) Log.Warn($"[hotfix] {w}");
        return new HotfixResult(tag, zip, notes, entries.Select(e => e.Name).ToList(), warnings);
    }

    private static string SourcesHint => Errors.CliOr("출처 보고(sources)를 확인한 뒤 올리세요.", "[출처] 화면을 확인한 뒤 올리세요.");

    // 패치 번호(태그)로 쓸 수 없으면 그 까닭 (폴더·릴리스 이름이 된다 — 점만 있거나 점으로 끝나면 폴더 이름이 바뀐다:
    // ".." 은 작업 폴더 밖, "7.58." 은 "7.58"). 화면이 실행 전에 같은 규칙으로 확인한다
    public static string? TagProblem(string tag) =>
        tag.Length == 0 || tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || tag.Contains(' ') || tag.EndsWith('.')
            ? $"패치 번호(태그)로 쓸 수 없습니다: '{tag}' (예: 7.58)" : null;

    // 클라이언트 버전 -> 패치 번호 (글로벌 먼저, 한국 전용 중간 패치처럼 대응표에 없는 버전은 건너뜀)
    public static string? PatchTag(IReadOnlyDictionary<string, string> gameVersions) =>
        gameVersions.OrderBy(kv => kv.Key == Region.Global.Name ? 0 : 1)
            .Select(kv => GameVersions.RetailOf(kv.Value)).FirstOrDefault(r => r != null);

    public static string Notes(Workspace ws, string tag, MaterialState prepared, BuildInfo built, IReadOnlyList<string> files)
    {
        static string Label(string region) => Region.Find(region)?.Label ?? region;
        string? deucalion = files.Select(f => System.Text.RegularExpressions.Regex.Match(f, @"^deucalion-(\d+(?:\.\d+)+)"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).FirstOrDefault();

        var sb = new StringBuilder();
        sb.AppendLine($"## {tag} 임시 패치본").AppendLine();
        sb.AppendLine("공식 FFXIV_ACT_Plugin 업데이트 전까지 쓰는 비공식 임시본입니다. FFLogs 업로드는 되지만 랭킹에는 반영되지 않습니다.");
        sb.AppendLine($"설치 방법: [README](https://github.com/{Repo}#설치)").AppendLine();
        sb.AppendLine("| | |").AppendLine("|---|---|");
        sb.AppendLine($"| 게임 버전 | {string.Join(" / ", prepared.GameVersions.GroupBy(kv => kv.Value).Select(g => $"{string.Join("·", g.Select(kv => Label(kv.Key)))} `{g.Key}`"))}" +
                      (prepared.FromServer ? " (공식 패치 서버의 exe 로 확인)" : "") + " |");
        string plugin = built.PluginVersion is { } pv
            ? $"[`{pv}`](https://github.com/ravahn/FFXIV_ACT_Plugin/releases/tag/{Uri.EscapeDataString(pv)})" : "`?`";
        sb.AppendLine($"| 공식 플러그인 | [FFXIV_ACT_Plugin](https://github.com/ravahn/FFXIV_ACT_Plugin) {plugin}" +
                      (deucalion != null ? $" (deucalion [`{deucalion}`](https://github.com/ff14wed/deucalion/releases/tag/{deucalion}))" : "") + " |");
        sb.AppendLine($"| 만든 도구 | [ActPluginRepack](https://github.com/{AppInfo.Repo}) `{AppInfo.DisplayVersion}` |");
        sb.AppendLine();

        sb.AppendLine("### 바꾼 것").AppendLine();
        // 같은 출처·결과인 지역은 한 줄로
        foreach (var g in prepared.Opcodes.GroupBy(kv => (kv.Value.Source, kv.Value.Changed, kv.Value.Total)))
            sb.AppendLine($"- opcode ({string.Join(", ", g.Select(kv => Label(kv.Key)))}): {OpcodeSourceLink(g.Key.Source)} — {g.Key.Total}개 중 {g.Key.Changed}개 변경");
        // 교체 시그니처 이름과 그 위의 자동 복구 출처 주석
        var hooks = SignatureInput.LoadWithComments(ws.SignaturesDir, SignatureInput.FileName);
        // 판단이 아니라 빌드가 실제로 넣는 것(input\signatures 의 교체 시그니처)으로 쓴다 — 직접 넣은 줄이 남아 있으면 공식이 맞아도 그것을 쓴다
        string why = prepared.HooksOkOfficially switch
        {
            false => "공식 시그니처가 맞지 않아 교체",
            true => "공식 시그니처도 맞지만 넣어 둔 교체 시그니처를 씀",
            null => "공식 시그니처는 점검하지 못함, 교체 시그니처를 씀",
        };
        sb.AppendLine(hooks.Count > 0
            ? $"- 패킷 후크: {why} — {string.Join(", ", hooks.Select(h => $"{h.Name} ({SignatureSourceLink(h.AutoComment)})"))}"
            : "- 패킷 후크: 공식 그대로");
        if (SignatureInput.LoadWithComments(ws.SignaturesDir, SignatureInput.MemoryFile) is { Count: > 0 } memory)
            sb.AppendLine($"- 메모리 시그니처: 교체 — {string.Join(", ", memory.Select(h => $"{h.Name} ({SignatureSourceLink(h.AutoComment)})"))}");
        if (SignatureInput.LoadWithComments(ws.SignaturesDir, SignatureInput.OodleFile) is { Count: > 0 } oodle)
            sb.AppendLine($"- Oodle 시그니처: 교체 — {string.Join(", ", oodle.Select(h => $"{h.Name} ({SignatureSourceLink(h.AutoComment)})"))}");
        // 이름 override (input\overrides): ACT 로그에서 가져온 RSV, 직접 넣은 파일 — build 가 넣는 것만 (Repacker 와 같은 규칙)
        if (Directory.Exists(ws.OverridesDir))
            foreach (string f in Directory.GetFiles(ws.OverridesDir, "*.txt").Where(f => Repacker.IsOverrideFile(Path.GetFileName(f)))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                int n = Rsv.Parse(File.ReadAllText(f)).Count;
                sb.AppendLine(Path.GetFileName(f).Equals(Rsv.FileName, StringComparison.OrdinalIgnoreCase)
                    ? $"- 가려진 이름(RSV): ACT 로그에서 가져온 {n}개 추가 (공식 플러그인에 없는 것만)"
                    : $"- 이름 override: `{Path.GetFileName(f)}` {n}개");
            }
        sb.AppendLine();
        // 교차 확인을 했고 다른 것도, 대조하지 못한 것도 없을 때만
        if (prepared.CrossCheck is { Count: 0 } && prepared.CrossCheckGaps is not { Count: > 0 })
        {
            sb.AppendLine("### 확인").AppendLine();
            sb.AppendLine(prepared.FromServer
                ? "- opcode·시그니처를 쓸 수 있었던 출처·수단(공개 자료, opcodediff 직접 실행, IDA, SigRegen 등)으로 대조해 다른 것 없음 " +
                  "(게임 데이터는 공식 플러그인 것 그대로)"
                : "- opcode·시그니처·게임 데이터(설치된 클라이언트)를 쓸 수 있었던 출처·수단(공개 자료, opcodediff 직접 실행, IDA, SigRegen, " +
                  "게임 데이터 추출기 등)으로 대조해 다른 것 없음");
            sb.AppendLine();
        }
        sb.AppendLine("### 알려진 문제").AppendLine().AppendLine("- 없음");
        return sb.ToString();
    }

    // opcode 출처 이름(OpcodeCollector 가 붙인 것) -> 쓴 파일로 가는 링크
    public static string OpcodeSourceLink(string source)
    {
        var (s, filled) = OpcodeSet.SplitFilled(source);
        string[] w = s.Split(' ');
        string link = w[0] switch
        {
            "Yarukon" when w.Length > 1 =>
                $"[Yarukon {w[1]}](https://github.com/Yarukon/FFXIVNetworkOpcodes/tree/master/output/{Uri.EscapeDataString(w[1])})",
            "opcodediff" when w.Length > 1 =>
                $"[opcodediff {w[1]}](https://github.com/xivdev/opcodediff/blob/master/diffs/{Uri.EscapeDataString(w[1])}.diff.json)",
            "karashiiro" => $"[karashiiro {string.Join(' ', w.Skip(1))}](https://github.com/karashiiro/FFXIVOpcodes/blob/master/opcodes.min.json)",
            "localdiff" => $"[opcodediff](https://github.com/xivdev/opcodediff) 직접 실행 ({string.Join(' ', w.Skip(1))})",
            "IDA" => $"IDA 직접 추출 ([finder](https://github.com/Yarukon/FFXIVNetworkOpcodes))",
            _ => s,
        };
        return filled ? $"{link} (빠진 항목은 다른 출처로 보충)" : link;
    }

    // 시그니처 줄 위의 "# auto: ..." (SignatureRepair 가 남긴 출처) -> 링크
    //   deucalion main a7316e7 (2026-10-13) - ...   -> 그 커밋
    //   deucalion release 1.5.1 - ...               -> 그 릴리스
    //   sigregen korean 2026.09.15 -> ...           -> 옛 exe 와 비교해 재생성 (링크 없음)
    public static string SignatureSourceLink(string? comment)
    {
        if (comment == null) return "직접 입력";
        var m = System.Text.RegularExpressions.Regex.Match(comment, @"^deucalion main ([0-9a-f]{7,40})");
        if (m.Success) return $"[deucalion `{m.Groups[1].Value}`](https://github.com/ff14wed/deucalion/commit/{m.Groups[1].Value})";
        m = System.Text.RegularExpressions.Regex.Match(comment, @"^deucalion release (\S+)");
        if (m.Success) return $"[deucalion {m.Groups[1].Value}](https://github.com/ff14wed/deucalion/releases/tag/{Uri.EscapeDataString(m.Groups[1].Value)})";
        if (comment.StartsWith("sigregen", StringComparison.Ordinal)) return "직전 버전 exe 와 비교해 재생성";
        return comment;
    }
}
