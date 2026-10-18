using System.Text.RegularExpressions;

// sigcheck 가 찾은 깨진 시그니처를 자동으로 고친다 (prepare 가 실행, --no-repair 로 끔).
//   1) upstream deucalion (ff14wed/deucalion) — 소스(main 의 lib.rs / create_target.rs 상수)와 최신 릴리스 dll 의 시그니처.
//      7.57 때 소스 수정이 공식 플러그인보다 약 5시간 빨랐다.
//   2) 자동 복구 — 옛 exe 와 새 exe 를 비교해 새 시그니처를 만든다 (SigRegen).
// 후보는 설정된 모든 클라이언트 exe 에서 검증해(0건이면 탈락) 통과한 것만 input\signatures 에 기록한다.
internal static class SignatureRepair
{
    // upstream 상수 이름 -> sigcheck 의 이름
    private static readonly Dictionary<string, string> SourceNames = new(StringComparer.Ordinal)
    {
        ["RECV_SIG"] = "Recv", ["SEND_SIG"] = "Send", ["SEND_LOBBY_SIG"] = "SendLobby",
        ["CREATE_TARGET_SIG"] = "CreateTarget.Normal", ["CREATE_TARGET_SIG_ALT"] = "CreateTarget.Alt",
        ["REG_FINDER_FALLBACK"] = "CreateTarget.Fallback",
    };
    private const string RawRepo = "https://raw.githubusercontent.com/ff14wed/deucalion"; // /<커밋>/deucalion/src/<파일>

    private sealed record Candidate(string Name, string Pattern, string From);

    // 고칠 때 쓰는 출처 (prepare --sources 로 고른다 — 한쪽만 시험할 때)
    public static readonly string[] Sources = ["upstream", "sigregen"];

    // 하나라도 기록했으면 true (호출자가 sigcheck 를 다시 돌린다). sources: 쓸 출처 (null 이면 전부).
    // failures: 후보·옛 exe 를 받다 난 실패를 모은다 (못 고쳤을 때 "받지 못해서"인지 화면이 안내하게)
    // regenerated: SigRegen 으로 다시 만들어 기록한 것의 이름을 모은다 (공개된 교체본이 아니라 화면이 따로 안내하게)
    public static bool Run(Workspace ws, List<GameClient> clients, Sigcheck.Result check, IReadOnlySet<string>? sources = null,
                           List<Exception>? failures = null, List<string>? regenerated = null)
    {
        failures ??= [];
        bool upstream = sources?.Contains("upstream") ?? true, sigregen = sources?.Contains("sigregen") ?? true;
        string sigDir = ws.SignaturesDir;
        var groups = check.Broken.GroupBy(b => b.Group).ToList();
        if (groups.Count == 0) return false;
        Log.Info();
        Log.Section($"[repair] 깨진 시그니처 {groups.Count}개 복구 시도");

        List<Candidate>? upstreamCandidates = null;
        bool changed = false;
        var exes = new ExeCache(); // exe 하나에 수백 MB — 이번 Run 에서만 쓰고 놓는다
        foreach (var group in groups)
        {
            Operation.ThrowIfCancelled();
            var original = group.First().Original;
            string kind = original.First().Item.Kind;
            var names = original.Select(r => r.Item.Name).Distinct().ToList();

            if (kind == SignatureKinds.Deucalion && upstream)
            {
                upstreamCandidates ??= Upstream(failures);
                var found = upstreamCandidates.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                    .Where(c => original.All(r => r.Pattern.Text != c.Pattern)) // 지금 것과 같으면 의미 없음
                    .Select(c => (c, counts: Validate(exes, c.Pattern, clients, kind, null, c.Name, original, expected: null)))
                    .FirstOrDefault(x => x.counts != null);
                if (found.counts != null)
                {
                    Record(sigDir, kind, found.c.Name, found.c.Pattern, $"{found.c.From} - {found.counts}");
                    changed = true;
                    continue;
                }
            }
            bool oldMatched = false;
            if (!sigregen)
            {
                Log.Warn($"[repair] {group.Key}: 고칠 후보를 찾지 못함 (SigRegen 은 출처에서 뺌)");
                continue;
            }
            if (Regenerate(ws, exes, group.ToList(), clients, failures, ref oldMatched) is { } regen)
            {
                Record(sigDir, kind, regen.Name, regen.Pattern, regen.From);
                regenerated?.Add(regen.Name);
                changed = true;
                continue;
            }
            if (!oldMatched && !original.First().Item.Critical)
                Log.Note($"[repair] {group.Key}: 이전 버전 exe 에서도 0건 - 원래부터 깨져 있던 것(공식 플러그인도 같음)이라 건너뜀");
            else
                Log.Warn($"[repair] {group.Key}: 고칠 후보를 찾지 못함");
        }
        return changed;
    }

    // exe 는 크고(.text 수십 MB, CodeImage 는 수백 MB) 같은 exe 를 여러 번 쓰므로 Run 동안만 캐시 (Run 마다 새로 만든다)
    private sealed class ExeCache
    {
        private readonly Dictionary<string, ScanTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CodeImage> _codes = new(StringComparer.OrdinalIgnoreCase);
        public ScanTarget Target(string path) => _targets.TryGetValue(path, out var t) ? t : _targets[path] = SigLib.Target(path);
        // 같은 exe 의 ScanTarget 을 함께 써서 파일과 .text 문자열을 두 번 들고 있지 않게 (exe 하나에 120MB 남짓)
        public CodeImage Code(string path) => _codes.TryGetValue(path, out var c) ? c : _codes[path] = SigLib.Run(() => new CodeImage(Target(path)));
        public void Forget(string path) => _targets.Remove(path); // 지울 exe
    }

    // 2) 옛 exe(원래 패턴이 맞던 버전)와 새 exe 를 비교해 다시 만든다. 깨진 클라이언트마다 시도하고,
    //    만든 패턴이 해당하는 모든 클라이언트에서 맞으면 채택.
    private static Candidate? Regenerate(Workspace ws, ExeCache exes, List<Sigcheck.Broken> broken, List<GameClient> clients,
                                         List<Exception> failures, ref bool oldMatched)
    {
        foreach (var b in broken)
        {
            foreach (var row in b.Original)
            {
                Operation.ThrowIfCancelled();
                string? oldExe = OldExe(ws, exes, b.Client, row.Pattern, walkBack: row.Item.Critical, failures);
                if (oldExe == null) continue;
                oldMatched = true;
                string variant = row.Item.Kind == SignatureKinds.Deucalion ? "*" : row.Item.Variant;
                Log.Note($"[repair] {b.Group}: {b.Client.Name} 옛 exe 와 비교해 재생성 ({row.Source} {row.Item.Name})");
                SigRegen.Result r;
                // 휴리스틱이라 예상 못 한 입력에서 터질 수 있다 — 이 항목만 실패로 두고 계속
                // memory / oodle 은 플러그인이 패턴 바로 뒤의 값(변위·rel32)을 읽는다 — 읽는 자리를 같은 명령어 필드로 따라가게
                // (그 자리가 옮겨지는 패턴은 만들지 않는다). deucalion 은 시작 위치나 call 대상을 쓰므로 따로 확인한다.
                int? readOffset = row.Item.Kind is SignatureKinds.Memory or SignatureKinds.Oodle ? 0 : null;
                try { r = SigRegen.Regenerate(exes.Code(oldExe), exes.Code(b.Client.ExePath), row.Pattern.Text, readOffset, SigLib.RegenDebug); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    Log.Warn($"[repair]   실패: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (r.Pattern == null)
                {
                    Log.Warn($"[repair]   실패: {r.Detail}");
                    continue;
                }
                // 플러그인은 패턴 바로 뒤(offset 0)의 값을 정해진 규칙으로 읽는다 — 읽는 자리나 RIP 변위 뒤의 길이(imm8 -> imm32 등)가
                // 바뀐 패턴은 쓰면 엉뚱한 주소를 읽는다
                if (readOffset != null && (r.Offset != 0 || r.RipOffset != r.OldRipOffset))
                {
                    Log.Warn($"[repair]   {r.Pattern} 는 읽는 자리가 바뀌어(offset {r.Offset}, rip 뒤 {r.OldRipOffset} -> {r.RipOffset}바이트) 쓰지 않음");
                    continue;
                }
                int expected = Sigcheck.Points(row.Pattern, exes.Target(oldExe), row.Item.Kind).Count; // 옛 exe 에서 가리키던 곳 수
                string? counts = Validate(exes, r.Pattern, clients, row.Item.Kind, variant == "*" ? null : variant, row.Item.Name,
                                          b.Original, expected);
                if (counts == null)
                {
                    Log.Warn($"[repair]   {r.Pattern} 는 다른 클라이언트에서 맞지 않거나 가리키는 곳이 다름");
                    continue;
                }
                string name = row.Item.Kind == SignatureKinds.Deucalion ? row.Item.Name : $"{row.Item.Variant}.{row.Item.Name}";
                string from = $"sigregen {b.Client.Name} {Path.GetFileName(Path.GetDirectoryName(oldExe))} -> {b.Client.Version} - {r.Detail} - {counts}";
                return new Candidate(name, r.Pattern, from);
            }
        }
        return null;
    }

    // 원래 패턴이 맞는 옛 exe: snapshots\<지역>\ 의 이전 버전(최신부터), 없으면 직전 출시(공식 패치 목록)를 받아서
    // walkBack: 직전 출시에서 안 맞으면 공식 패치 목록을 더 거슬러 올라간다 (패킷 후크만 — 경고 항목까지
    // 하면 원래부터 깨진 시그니처 때문에 exe 를 여러 번 받는다)
    private static string? OldExe(Workspace ws, ExeCache exes, GameClient client, SigPattern pattern, bool walkBack, List<Exception> failures)
    {
        if (client.Region == null || client.Version == null) return null;
        var paths = new List<string>();
        string dir = ws.SnapshotRegionDir(client.Region);
        // 공식 패치 서버에서 받는 지역의 스냅샷은 받아 둔 것이므로 서명이 맞는 것만 (대만판은 이 PC 의 게임에서 복사한 것)
        if (Directory.Exists(dir))
            paths.AddRange(Directory.GetDirectories(dir)
                .Where(d => GameVersion.IsValid(Path.GetFileName(d)) && string.CompareOrdinal(Path.GetFileName(d), client.Version) < 0)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "ffxiv_dx11.exe")).Where(File.Exists)
                .Where(p => !Thaliak.Supports(client.Region) || Thaliak.Trusted(p)));
        foreach (string p in paths)
            if (Matches(exes, pattern, p)) return p;

        // 받아 볼 버전: 직전 출시, 그다음(walkBack) 공식 패치 목록에서 앞선 패치들. 원래 패턴이 맞는 exe 를 찾으면 멈춘다.
        if (!Thaliak.Supports(client.Region)) return null;
        var tried = paths.Select(p => Path.GetFileName(Path.GetDirectoryName(p))!).ToHashSet();
        var candidates = new List<string>();
        string? previous = null;
        try
        {
            previous = Thaliak.PreviousRelease(client.Region, client.Version);
            if (previous != null) candidates.Add(previous);
            if (walkBack) candidates.AddRange(Thaliak.EarlierVersions(client.Region, client.Version).Take(4));
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Log.Warn($"[repair] 공식 패치 목록을 받지 못함: {Errors.Describe(ex)}");
            failures.Add(ex);
        }
        foreach (string version in candidates.Distinct().Where(v => !tried.Contains(v)))
        {
            Operation.ThrowIfCancelled();
            string folder = ws.SnapshotDir(client.Region, version);
            bool fresh = !Directory.Exists(folder);
            string p;
            try { p = Thaliak.SnapshotExe(ws, client.Region, version); }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                Log.Note($"[repair] {client.Name} 옛 exe({version})를 받지 못함: {ex.Message}");
                failures.Add(ex);
                continue;
            }
            // 받은 뒤의 실패(스캔 시간 초과 등)는 받기 실패가 아니다 — 더 받아 봐도 같으므로 멈춘다 (거슬러 올라가며 받은 것은 지우고)
            if (!TryMatches(exes, pattern, p, out bool matched))
            {
                if (fresh && version != previous)
                {
                    exes.Forget(p);
                    Files.DeleteDirectoryQuietly(folder);
                }
                return null;
            }
            try
            {
                if (matched) return p;
                Log.Note($"[repair] {client.Name} {version} exe 에서도 원래 패턴이 0건 - 더 앞 버전 시도");
                // 직전 출시 버전 exe 는 다른 항목에도 쓰이므로 남기고, 거슬러 올라가며 받은 것만 지운다
                if (fresh && version != previous)
                {
                    exes.Forget(p);
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (Errors.IsFileError(ex))
            {
                Log.Note($"[repair] 받은 옛 exe 폴더를 지우지 못함: {ex.Message}");
            }
        }
        return null;
    }

    // 원래 패턴이 이 exe 에 맞는지. 스캔이 실패하면(시간 초과 — 지나치게 넓은 패턴) 알리고 false
    private static bool Matches(ExeCache exes, SigPattern pattern, string exe) => TryMatches(exes, pattern, exe, out bool m) && m;

    private static bool TryMatches(ExeCache exes, SigPattern pattern, string exe, out bool matched)
    {
        try
        {
            matched = pattern.Scan(exes.Target(exe)).Count > 0;
            return true;
        }
        catch (SignatureException ex)
        {
            Log.Warn($"[repair] 옛 exe 에서 원래 패턴을 찾지 못함: {ex.Message}");
            matched = false;
            return false;
        }
        catch (Exception ex) when (Errors.IsToolOrFileError(ex)) // 이 exe 를 읽지 못함 (깨짐, 백신 잠금) — 다른 버전은 해 본다
        {
            Log.Note($"[repair] 옛 exe 를 읽지 못함: {ex.Message}");
            matched = false;
            return true;
        }
    }

    private static void Record(string sigDir, string kind, string name, string pattern, string why)
    {
        SignatureInput.Upsert(sigDir, SignatureInput.FileFor(kind), name, pattern, why);
        Log.Done($@"[repair] {name} <- {pattern}  ({why}) -> input\signatures\{SignatureInput.FileFor(kind)}");
    }

    // 후보 검증. 통과하면 "클라이언트: 곳 수" 요약, 아니면 null.
    //   원래 패턴(같은 항목)이 아직 맞는 클라이언트: 후보가 정확히 같은 곳들을 가리켜야 한다 — 거기서 다른 곳을
    //     가리키면 그 클라이언트를 망가뜨린다 (input 은 모든 클라이언트에 적용됨).
    //   원래 패턴이 깨진 클라이언트: 1곳 이상, expected 가 있으면 정확히 그 수. 그리고 그런 클라이언트끼리 수가 같아야
    //     한다 — deucalion 은 맞는 곳을 전부 후킹하므로 넓은 패턴은 엉뚱한 함수까지 건드린다.
    private static string? Validate(ExeCache exes, string pattern, List<GameClient> clients, string kind, string? variant, string itemName,
                                    IReadOnlyList<Sigcheck.Row> original, int? expected)
    {
        var sig = SigLib.Parse(pattern);
        var parts = new List<string>();
        var brokenCounts = new HashSet<int>();
        foreach (var client in clients)
        {
            if (variant != null && !Sigcheck.Applies(kind, variant, client.Region)) continue;
            var exe = exes.Target(client.ExePath);
            var got = Sigcheck.Points(sig, exe, kind);
            if (got.Count == 0) return null;
            var reference = original.Where(r => r.Item.Name == itemName)
                .Select(r => Sigcheck.Points(r.Pattern, exe, kind)).FirstOrDefault(p => p.Count > 0);
            if (reference != null)
            {
                if (!got.SetEquals(reference)) return null;
            }
            else
            {
                if (expected != null && got.Count != expected) return null;
                brokenCounts.Add(got.Count);
            }
            parts.Add($"{client.Name} {got.Count}");
        }
        if (brokenCounts.Count > 1) return null;
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // upstream deucalion 의 시그니처: 소스 main 이 먼저(가장 최신), 그다음 최신 릴리스 dll
    private static List<Candidate> Upstream(List<Exception> failures)
    {
        var list = new List<Candidate>();
        try
        {
            // 이름표에 적는 그 커밋에서 받는다 (확인한 커밋과 받은 내용이 어긋나지 않게 — 커밋을 모르면 main)
            string label = "deucalion main", rev = "main";
            if (GitHub.RecentCommits("ff14wed/deucalion", "main", null, 1).FirstOrDefault() is { } c)
                (label, rev) = ($"deucalion main {c.Sha[..7]} ({c.Date:yyyy-MM-dd})", c.Sha);
            foreach (string file in new[] { "lib.rs", "hook/create_target.rs" })
                if (Http.GetStringOrNull($"{RawRepo}/{rev}/deucalion/src/{file}") is { } src)
                    foreach (Match m in Regex.Matches(src, @"const\s+(\w+)\s*:\s*&str\s*=\s*""([^""]+)"""))
                        if (SourceNames.TryGetValue(m.Groups[1].Value, out string? name))
                        {
                            // 이 도구가 모르는 문법이면 그 후보만 뺀다 (하나 때문에 복구 전체가 멈추지 않게)
                            if (SigPattern.TryParse(m.Groups[2].Value, out _)) list.Add(new Candidate(name, m.Groups[2].Value, label));
                            else Log.Note($"[repair] {label} 의 {m.Groups[1].Value} 는 읽을 수 없는 패턴이라 후보에서 뺌: {m.Groups[2].Value}");
                        }
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Log.Warn($"[repair] deucalion 소스를 받지 못함: {Errors.Describe(ex)}");
            failures.Add(ex);
        }

        try
        {
            if (GitHubRelease.Latest("ff14wed/deucalion") is { } release &&
                release.AssetUrl(n => n == "deucalion.dll") is { } url && Http.GetBytesOrNull(url) is { } dll)
                list.AddRange(Deucalion.ReadSignatures(dll).Select(s => new Candidate(s.Name, s.Pattern, $"deucalion release {release.Tag}")));
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Log.Warn($"[repair] deucalion 릴리스를 받지 못함: {Errors.Describe(ex)}");
            failures.Add(ex);
        }
        return list;
    }
}
