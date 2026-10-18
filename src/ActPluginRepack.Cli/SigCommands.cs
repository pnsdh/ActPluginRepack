using System.Text;
using System.Text.Json;

// 범용 시그니처 명령 (sigscan, sigregen). 다른 도구(OverlayPlugin / cactbot 의 점검 도구)가 부르는 약속이기도 하다:
//   패턴 목록 파일 (JSON 배열):
//     [ { "name": "...", "pattern": "...", "syntax": "hex" | "pelite", "offset": <int>, "rip": <bool>, "ripOffset": <int> }, ... ]
//     syntax 기본값 hex (두 글자 = 1바이트, "??" = 와일드카드). offset 이 없으면 값은 해석하지 않는다. 모르는 필드는 무시.
//   --json 출력은 "format": 1. 필드는 추가만 하고 빼거나 의미를 바꾸지 않는다 (바꿔야 하면 format 을 올린다).
//   주소는 "0x" 를 붙인 16진수 RVA 문자열. --json 이면 진행 메시지는 stderr 로 간다.
internal static class SigCommands
{
    private const int Format = 1;
    private const int MaxHits = 16;

    // 종료 코드 (이 두 명령만의 뜻 — 다른 도구가 읽으므로 값은 바꾸지 않는다. 나머지는 ExitCodes)
    private const int ScanNoMatch = 2;        // sigscan: 0곳에 맞는 패턴이 있음
    private const int ScanManyMatches = 3;    // sigscan: 0곳은 없고 여러 곳에 맞는 패턴이 있음
    private const int RegenFailed = 2;        // sigregen --patterns: 만들지 못한 패턴이 있음

    private sealed record Spec(string Name, string Pattern, SigSyntax Syntax, int? Offset, bool Rip, int RipOffset)
    {
        public string Pelite => Syntax == SigSyntax.Hex ? SigPattern.HexToPelite(Pattern) : Pattern;
    }

    // ---- sigscan ----

    public static int Scan(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--patterns", "--exe", "--region", "--version"], ["--json", "--all-sections"]);
        using var _ = JsonMode(o);
        bool json = o.Flag("--json");
        var specs = LoadSpecs(o.Need("--patterns"));
        RegionOnlyWithVersion(o, "--version");
        var exe = ResolveExe(o, "--exe", "--version");
        var scope = o.Flag("--all-sections") ? ScanScope.AllSections : ScanScope.Text;
        var target = SigLib.Run(() => new ScanTarget(exe.Path, scope));

        var results = specs.Select(s =>
        {
            var matches = SigLib.Run(() => SigPattern.Parse(s.Pelite)).Scan(target);
            var values = s.Offset is { } off
                ? matches.Take(MaxHits).Select(m => SigValue.Resolve(target.Image, m, off, s.Rip, s.RipOffset)).ToList()
                : null;
            return (Spec: s, Matches: matches, Values: values);
        }).ToList();

        bool anyError = results.Any(r => r.Values?.Any(v => v.Error != null) == true);
        // 값을 해석하지 못한 것이 있으면 1 (--json 결과는 그래도 낸다)
        int code = anyError ? ExitCodes.Error
            : results.Any(r => r.Matches.Count == 0) ? ScanNoMatch
            : results.Any(r => r.Matches.Count > 1) ? ScanManyMatches : ExitCodes.Ok;

        if (json)
        {
            WriteJson(w =>
            {
                w.WriteNumber("format", Format);
                w.WriteNumber("apiVersion", SignatureLibrary.ApiVersion);
                w.WriteString("exe", exe.Path);
                w.WriteString("gameVersion", exe.Version);
                w.WriteString("scope", scope == ScanScope.Text ? "text" : "all");
                w.WriteString("imageBase", Hex(target.Image.ImageBase));
                w.WriteStartArray("results");
                foreach (var (s, matches, values) in results)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name);
                    w.WriteString("pattern", s.Pattern);
                    w.WriteNumber("matches", matches.Count);
                    w.WriteStartArray("hits");
                    for (int i = 0; i < Math.Min(matches.Count, MaxHits); i++)
                    {
                        var m = matches[i];
                        var v = values?[i];
                        w.WriteStartObject();
                        w.WriteString("rva", Hex(m.Rva));
                        w.WriteString("matchSection", m.Section);
                        w.WriteBoolean("crosses64k", m.CrossesBoundary(0x10000));
                        if (s.Offset is { } so) w.WriteBoolean("missedByChunkScan", m.MissedByChunkedScan(so, s.Rip));
                        else w.WriteNull("missedByChunkScan");
                        // 읽을 자리가 이미지 밖이면 (offset 이 패턴 앞으로 너무 나감 등) null
                        w.WriteString("readRva", v == null || (v.ReadRva == 0 && v.Error != null) ? null : Hex(v.ReadRva));
                        w.WriteString("resolvedRva", v?.ResolvedRva is { } rv ? Hex(rv) : null);
                        w.WriteString("section", v?.Section);
                        w.WriteString("error", v?.Error);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            return code;
        }

        Log.Section($"[sigscan] {exe.Path}{(exe.Version != null ? $" ({exe.Version})" : "")} - {(scope == ScanScope.Text ? ".text" : "모든 섹션")}");
        int width = specs.Max(s => s.Name.Length);
        foreach (var (s, matches, values) in results)
        {
            string head = $"  {s.Name.PadRight(width)}  {matches.Count}곳";
            // Error 는 stderr 로 가서 표 순서가 섞이므로 0곳도 Warn (종료 코드로 구분)
            var kind = matches.Count == 1 ? LogKind.Info : LogKind.Warn;
            Log.Write(kind, head);
            for (int i = 0; i < Math.Min(matches.Count, MaxHits); i++)
            {
                var m = matches[i];
                bool missed = s.Offset is { } so ? m.MissedByChunkedScan(so, s.Rip) : m.CrossesBoundary(0x10000);
                string where = $"    {Hex(m.Rva)} {m.Section}{(missed ? " (64KB 덩어리 경계 — 런타임 스캐너가 놓침)" : "")}";
                if (values?[i] is { } v)
                    where += v.Error != null ? $"  -> 해석 실패: {v.Error}" : $"  -> {Hex(v.ResolvedRva!.Value)} {v.Section ?? "(섹션 밖)"}";
                Log.Write(HitKind(values?[i]), where);
            }
            if (matches.Count > MaxHits) Log.Note($"    ... {matches.Count - MaxHits}곳 더");
        }
        return code;

        static LogKind HitKind(SigValue? v) => v?.Error != null || (v != null && v.Section == null) ? LogKind.Warn : LogKind.Note;
    }

    // ---- sigregen ----

    // 하나: --old --new --pattern [--truth] (pelite 표기, 사람용)
    // 여러 개: --patterns <json> [--json] — 패턴마다 새 패턴(입력과 같은 표기)과 새 offset
    public static int Regen(string[] args)
    {
        var o = Options.Parse(args, ["--root", "--old", "--new", "--region", "--old-version", "--new-version", "--pattern", "--truth", "--patterns"],
                              ["--json"]);
        RegionOnlyWithVersion(o, "--old-version", "--new-version");
        if (o.Get("--patterns") == null) return SingleRegen(o);
        if (o.Get("--pattern") != null || o.Get("--truth") != null)
            throw new ToolException("--patterns 는 --pattern / --truth 와 함께 쓸 수 없습니다.");

        using var _ = JsonMode(o);
        bool json = o.Flag("--json");
        var specs = LoadSpecs(o.Need("--patterns"));
        var oldExe = ResolveExe(o, "--old", "--old-version");
        var newExe = ResolveExe(o, "--new", "--new-version");
        Log.Note($"[sigregen] 옛 {oldExe.Path} / 새 {newExe.Path} 읽는 중");
        var oldCode = SigLib.Code(oldExe.Path);
        var newCode = SigLib.Code(newExe.Path);

        var results = new List<(Spec Spec, SigRegen.Result R, string? Pattern, string Detail)>();
        foreach (var s in specs)
        {
            Operation.ThrowIfCancelled();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SigRegen.Result r;
            // 휴리스틱이라 예상 못 한 입력에서 터질 수 있다 — 그 패턴만 실패로 두고 계속 (prepare 의 자동 복구와 같게)
            try { r = FFXIV.Signatures.SigRegen.Regenerate(oldCode, newCode, s.Pelite, s.Offset, SigLib.RegenDebug); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                r = new SigRegen.Result(null, ex is SignatureException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            }
            string? pattern = r.AlreadyMatches ? s.Pattern : null;
            string detail = r.Detail;
            if (r.Pattern != null)
            {
                try { pattern = s.Syntax == SigSyntax.Hex ? SigPattern.PeliteToHex(r.Pattern) : r.Pattern; }
                catch (SignatureException ex) { detail += $" - {ex.Message}"; }
            }
            results.Add((s, r, pattern, detail));
            Log.Write(pattern != null ? LogKind.Info : LogKind.Warn,
                $"[sigregen] {s.Name}: {pattern ?? "(실패)"}{(r.Offset is { } ro && pattern != null ? $" offset {ro}" : "")}{(r.RipOffset is { } rp && pattern != null ? $" ripOffset {rp}" : "")}  - {detail} ({sw.Elapsed.TotalSeconds:0.0}s)");
        }
        int code = results.All(x => x.Pattern != null) ? ExitCodes.Ok : RegenFailed;

        if (json)
        {
            WriteJson(w =>
            {
                w.WriteNumber("format", Format);
                w.WriteNumber("apiVersion", SignatureLibrary.ApiVersion);
                w.WriteString("old", oldExe.Path);
                w.WriteString("oldVersion", oldExe.Version);
                w.WriteString("new", newExe.Path);
                w.WriteString("newVersion", newExe.Version);
                w.WriteStartArray("results");
                foreach (var (s, r, pattern, detail) in results)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name);
                    w.WriteBoolean("ok", pattern != null);
                    // 패턴이 그대로 맞고 읽는 자리(offset, ripOffset)도 그대로일 때만 — 패턴은 그대로여도 offset 이 바뀔 수 있다
                    w.WriteBoolean("unchanged", r.AlreadyMatches && (s.Offset == null || (r.Offset == s.Offset && (!s.Rip || r.RipOffset == s.RipOffset))));
                    w.WriteString("pattern", pattern);
                    w.WriteString("pelitePattern", r.Pattern ?? (r.AlreadyMatches ? s.Pelite : null));
                    if (pattern != null && r.Offset is { } off) w.WriteNumber("offset", off); else w.WriteNull("offset");
                    if (pattern != null && r.RipOffset is { } rip) w.WriteNumber("ripOffset", rip); else w.WriteNull("ripOffset");
                    w.WriteString("readField", r.ReadField);
                    w.WriteBoolean("callTargetUnverified", r.CallTargetUnverified);
                    w.WriteString("detail", detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
        return code;
    }

    // 옛 exe 에서 맞던 시그니처를 새 exe 용으로 다시 만든다 (--truth: 정답 시그니처와 같은 곳을 가리키는지 비교)
    private static int SingleRegen(Options o)
    {
        if (o.Flag("--json")) throw new ToolException("--json 은 --patterns 와 함께만 씁니다.");
        string pattern = o.Get("--pattern") ?? throw new ToolException("--pattern <시그니처> 또는 --patterns <json> 이 필요합니다.");
        SigLib.Parse(pattern); // exe 를 읽기 전에 문법부터
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var oldExe = SigLib.Code(ResolveExe(o, "--old", "--old-version").Path);
        var newExe = SigLib.Code(ResolveExe(o, "--new", "--new-version").Path);
        var r = FFXIV.Signatures.SigRegen.Regenerate(oldExe, newExe, pattern, debug: SigLib.RegenDebug);
        Log.Info($"[sigregen] {r.Pattern ?? (r.AlreadyMatches ? "(그대로 써도 됨)" : "(실패)")}  - {r.Detail} ({sw.Elapsed.TotalSeconds:0.0}s)");
        // 패턴 하나일 때는 만들지 못하면 1 (--patterns 의 RegenFailed 와 다르다 — 문서의 표)
        if (o.Get("--truth") is not { } truth || r.Pattern == null) return r.Pattern != null || r.AlreadyMatches ? ExitCodes.Ok : ExitCodes.Error;

        static string Where(List<SigPattern.Match> ms) =>
            string.Join(",", ms.Select(m => m.Saves.Count > 0 ? $"{m.Rva:x}->{m.Saves[0]:x}" : $"{m.Rva:x}..{m.End:x}").Order());
        var mine = SigLib.Parse(r.Pattern).Scan(newExe.Target);
        var want = SigLib.Parse(truth).Scan(newExe.Target);
        // 패턴 시작점은 정답과 다를 수 있으니(정답이 다른 곳에서 시작) call 대상, 아니면 시작 또는 끝 위치 집합으로 비교
        // (후크는 시작, 메모리 시그니처는 패턴 바로 뒤를 읽으므로 끝이 중요)
        static bool SameSet(IEnumerable<int> x, IEnumerable<int> y) => x.Order().SequenceEqual(y.Order());
        bool same = mine.Count == want.Count &&
            (mine.All(m => m.Saves.Count > 0) && want.All(m => m.Saves.Count > 0)
                ? SameSet(mine.Select(m => m.Saves[0]), want.Select(m => m.Saves[0]))
                : SameSet(mine.Select(m => m.Rva), want.Select(m => m.Rva)) || SameSet(mine.Select(m => m.End), want.Select(m => m.End)));
        Log.Info($"[sigregen] 정답: {Where(want)}  /  복구: {Where(mine)}  => {(same ? "일치" : "불일치")}");
        return same ? ExitCodes.Ok : ExitCodes.Error;
    }

    // ---- 공통 ----

    // --json 이면 stdout 은 JSON 만: 진행 메시지와 오류(error: ...)는 stderr 로, 부르는 도구가 읽기 쉽게 UTF-8 로
    private static IDisposable? JsonMode(Options o)
    {
        if (!o.Flag("--json")) return null;
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        return Operation.Begin(StderrReporter.Instance, Operation.Token);
    }

    // --region 은 버전으로 exe 를 고를 때만 뜻이 있다 — --exe 와 함께 주면 무시되는 대신 알린다
    private static void RegionOnlyWithVersion(Options o, params string[] versionOpts)
    {
        if (o.Get("--region") != null && versionOpts.All(v => o.Get(v) == null))
            throw new ToolException($"--region 은 버전({string.Join(" / ", versionOpts)})으로 exe 를 고를 때만 씁니다 (exe 를 직접 주면 필요 없음).");
    }

    // 게임 버전 인자: 2026.10.06.0000.0000 그대로, 아니면 패치 번호(7.57)로 보고 대응표에서 찾는다
    internal static string GameVersionArg(string value)
    {
        if (GameVersion.IsValid(value)) return value;
        string? v = GameVersions.VersionOf(value);
        return GameVersion.IsValid(v) ? v! : throw new ToolException($"게임 버전 형식이 아니거나 모르는 패치 번호입니다: {value} (예: 2026.10.06.0000.0000 또는 7.57)");
    }

    // exe: --<exeOpt> <파일> 또는 --region <지역> --<versionOpt> <게임 버전 | 패치 번호> (작업 폴더 snapshots\ 의 보관본,
    // 없으면 Thaliak 에서 받음 — 그때만 작업 폴더를 잠근다)
    private static (string Path, string? Version) ResolveExe(Options o, string exeOpt, string versionOpt)
    {
        string? exe = o.Get(exeOpt), version = o.Get(versionOpt);
        if (exe != null && version != null) throw new ToolException($"{exeOpt} 와 {versionOpt} 중 하나만 쓰세요.");
        if (exe != null)
        {
            if (!File.Exists(exe)) throw new ToolException($"exe 파일이 없습니다: {exe}");
            string full = Path.GetFullPath(exe);
            return (full, GameExe.ReadVersion(full));
        }
        if (version == null) throw new ToolException($"{exeOpt} <exe> 또는 --region <지역> {versionOpt} <게임 버전> 이 필요합니다.");
        var region = o.Region("--region");
        string v = GameVersionArg(version);
        var ws = o.Workspace();
        if (Thaliak.CachedSnapshotExe(ws, region, v) is { } cached) return (cached, v);
        if (!Thaliak.Supports(region))
            throw new ToolException($"{region.Label} exe 는 공식 패치 서버에서 받을 수 없고, 보관본도 없습니다: {ws.SnapshotDir(region, v)}");
        using var _ = ws.Lock();
        return (Thaliak.SnapshotExe(ws, region, v), v);
    }

    private static List<Spec> LoadSpecs(string path)
    {
        if (!File.Exists(path)) throw new ToolException($"패턴 목록 파일이 없습니다: {path}");
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (Errors.IsFileError(ex)) { throw new ToolException($"패턴 목록 파일을 읽지 못했습니다: {path} ({ex.Message})"); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, JsonOptions.Lenient); }
        catch (JsonException ex) { throw new ToolException($"패턴 목록 파일이 JSON 이 아닙니다: {path} ({ex.Message})"); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ToolException($"패턴 목록 파일은 JSON 배열이어야 합니다: {path}");
            var specs = new List<Spec>();
            int i = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string where = $"{path} [{i++}]";
                if (e.ValueKind != JsonValueKind.Object) throw new ToolException($"{where}: 객체가 아닙니다.");
                string name = Str(e, "name", where) ?? throw new ToolException($"{where}: name 이 없습니다.");
                string pattern = Str(e, "pattern", where) ?? throw new ToolException($"{where} ({name}): pattern 이 없습니다.");
                var syntax = (Str(e, "syntax", where) ?? "hex").ToLowerInvariant() switch
                {
                    "hex" => SigSyntax.Hex,
                    "pelite" => SigSyntax.Pelite,
                    var x => throw new ToolException($"{where} ({name}): syntax 는 hex 또는 pelite 입니다 ('{x}')."),
                };
                int? offset = Int(e, "offset", where);
                bool? rip = e.TryGetProperty("rip", out var r) && r.ValueKind != JsonValueKind.Null
                    ? r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : throw new ToolException($"{where} ({name}): rip 은 true/false 입니다.")
                    : null;
                int? ripOffset = Int(e, "ripOffset", where);
                if (offset == null && (rip != null || ripOffset != null))
                    throw new ToolException($"{where} ({name}): rip / ripOffset 은 offset 과 함께 씁니다.");
                var spec = new Spec(name, pattern, syntax, offset, rip ?? false, ripOffset ?? 0);
                try { SigPattern.Parse(spec.Pelite); }
                catch (SignatureException ex) { throw new ToolException($"{where} ({name}): {ex.Message}"); }
                if (specs.Any(s => s.Name == name)) throw new ToolException($"{where}: 이름이 겹칩니다 ({name}).");
                specs.Add(spec);
            }
            if (specs.Count == 0) throw new ToolException($"패턴 목록이 비어 있습니다: {path}");
            return specs;
        }
    }

    private static string? Str(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : throw new ToolException($"{where}: {key} 는 문자열이어야 합니다.");
    }

    private static int? Int(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : throw new ToolException($"{where}: {key} 는 정수여야 합니다.");
    }

    private static string Hex(long v) => $"0x{v:x}";
    private static string Hex(ulong v) => $"0x{v:x}";

    // 출력은 ASCII 만 (한글은 \uXXXX) — 콘솔 코드 페이지와 상관없이 그대로 읽히게
    private static void WriteJson(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        Console.Out.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
    }

    // --json 일 때 진행 메시지를 전부 stderr 로 (JsonMode 가 stderr 를 UTF-8 로 바꿔 둔다)
    private sealed class StderrReporter : IReporter
    {
        public static readonly StderrReporter Instance = new();
        private static readonly Lock Gate = new();

        public void Log(LogKind kind, string message)
        {
            lock (Gate) Console.Error.WriteLine(message);
        }
    }
}
