using System.Text.Json;
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
    public static bool Run(Workspace ws, List<GameClient> clients, Sigcheck.Result check, IReadOnlySet<string>? sources = null,
                           List<Exception>? failures = null)
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
        _targets.Clear();
        _codes.Clear();
        try
        {
            foreach (var group in groups)
            {
                Operation.ThrowIfCancelled();
                var original = group.First().Original;
                string kind = original.First().Item.Kind;
                var names = original.Select(r => r.Item.Name).Distinct().ToList();

                if (kind == "deucalion" && upstream)
                {
                    upstreamCandidates ??= Upstream(failures);
                    var found = upstreamCandidates.Where(c => names.Contains(c.Name))
                        .Where(c => original.All(r => r.Pattern.Text != c.Pattern)) // 지금 것과 같으면 의미 없음
                        .Select(c => (c, counts: Validate(c.Pattern, clients, kind, null, c.Name, original, expected: null)))
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
                if (Regenerate(ws, group.ToList(), clients, failures, ref oldMatched) is { } regen)
                {
                    Record(sigDir, kind, regen.Name, regen.Pattern, regen.From);
                    changed = true;
                    continue;
                }
                if (!oldMatched && !original.First().Item.Critical)
                    Log.Note($"[repair] {group.Key}: 이전 버전 exe 에서도 0건 - 원래부터 깨져 있던 것(공식 플러그인도 같음)이라 건너뜀");
                else
                    Log.Warn($"[repair] {group.Key}: 고칠 후보를 찾지 못함");
            }
        }
        finally
        {
            _targets.Clear();
            _codes.Clear(); // exe 하나에 수백 MB — 끝나면(취소·실패해도) 놓는다
        }
        return changed;
    }

    // exe 는 크고(.text 수십 MB, CodeImage 는 수백 MB) 같은 exe 를 여러 번 쓰므로 Run 동안만 캐시
    private static readonly Dictionary<string, ScanTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CodeImage> _codes = new(StringComparer.OrdinalIgnoreCase);
    private static ScanTarget Target(string path) => _targets.TryGetValue(path, out var t) ? t : _targets[path] = new ScanTarget(path);
    private static CodeImage Code(string path) => _codes.TryGetValue(path, out var c) ? c : _codes[path] = new CodeImage(path);

    // 패턴이 가리키는 곳: call 형은 따라간 대상(deucalion 은 가장 깊은 북마크를 쓴다), memory/oodle 은 패턴 끝
    // (플러그인이 그 바로 뒤를 읽음), deucalion 의 나머지는 시작 위치(거기를 후킹)
    private static HashSet<int> Points(SigPattern sig, ScanTarget exe, string kind) =>
        sig.Scan(exe).Select(m => m.Saves.Count > 0 ? m.Saves[^1] : kind == "deucalion" ? m.Rva : m.End).ToHashSet();

    // 2) 옛 exe(원래 패턴이 맞던 버전)와 새 exe 를 비교해 다시 만든다. 깨진 클라이언트마다 시도하고,
    //    만든 패턴이 해당하는 모든 클라이언트에서 맞으면 채택.
    private static Candidate? Regenerate(Workspace ws, List<Sigcheck.Broken> broken, List<GameClient> clients,
                                         List<Exception> failures, ref bool oldMatched)
    {
        foreach (var b in broken)
        {
            foreach (var row in b.Original)
            {
                Operation.ThrowIfCancelled();
                string? oldExe = OldExe(ws, b.Client, row.Pattern, walkBack: row.Item.Critical, failures);
                if (oldExe == null) continue;
                oldMatched = true;
                string variant = row.Item.Kind == "deucalion" ? "*" : row.Item.Variant;
                Log.Note($"[repair] {b.Group}: {b.Client.Name} 옛 exe 와 비교해 재생성 ({row.Source} {row.Item.Name})");
                SigRegen.Result r;
                // 휴리스틱이라 예상 못 한 입력에서 터질 수 있다 — 이 항목만 실패로 두고 계속
                try { r = SigRegen.Regenerate(Code(oldExe), Code(b.Client.ExePath), row.Pattern.Text); }
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
                int expected = Points(row.Pattern, Target(oldExe), row.Item.Kind).Count; // 옛 exe 에서 가리키던 곳 수
                string? counts = Validate(r.Pattern, clients, row.Item.Kind, variant == "*" ? null : variant, row.Item.Name,
                                          b.Original, expected);
                if (counts == null)
                {
                    Log.Warn($"[repair]   {r.Pattern} 는 다른 클라이언트에서 맞지 않거나 가리키는 곳이 다름");
                    continue;
                }
                string name = row.Item.Kind == "deucalion" ? row.Item.Name : $"{row.Item.Variant}.{row.Item.Name}";
                string from = $"sigregen {b.Client.Name} {Path.GetFileName(Path.GetDirectoryName(oldExe))} -> {b.Client.Version} - {r.Detail} - {counts}";
                return new Candidate(name, r.Pattern, from);
            }
        }
        return null;
    }

    // 원래 패턴이 맞는 옛 exe: snapshots\<지역>\ 의 이전 버전(최신부터), 없으면 대응표의 직전 버전을 받아서
    // walkBack: 대응표의 직전 버전에서 안 맞으면 공식 패치 목록을 더 거슬러 올라간다 (패킷 후크만 — 경고 항목까지
    // 하면 원래부터 깨진 시그니처 때문에 exe 를 여러 번 받는다)
    private static string? OldExe(Workspace ws, GameClient client, SigPattern pattern, bool walkBack, List<Exception> failures)
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
            if (pattern.Scan(Target(p)).Count > 0) return p;

        // 받아 볼 버전: 대응표의 직전 출시 버전, 그다음 공식 패치 목록에서 앞선 패치들 (대응표에 없는 지역 전용
        // 중간 패치 등). 원래 패턴이 맞는 exe 를 찾으면 멈춘다 — 미리 배포된 패치는 이미 새 exe 라 걸러진다.
        if (!Thaliak.Supports(client.Region)) return null;
        var tried = paths.Select(p => Path.GetFileName(Path.GetDirectoryName(p))!).ToHashSet();
        var candidates = new List<string>();
        string? previous = GameVersions.Previous(client.Version)?.Version;
        if (previous != null) candidates.Add(previous);
        else if (GameVersions.LastFailure is { } tableFailure && !failures.Contains(tableFailure)) failures.Add(tableFailure);
        if (walkBack || previous == null)
        {
            try { candidates.AddRange(Thaliak.EarlierVersions(client.Region, client.Version).Take(walkBack ? 4 : 1)); }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                Log.Warn($"[repair] 공식 패치 목록을 받지 못함: {Errors.Describe(ex)}");
                failures.Add(ex);
            }
        }
        foreach (string version in candidates.Distinct().Where(v => !tried.Contains(v)))
        {
            Operation.ThrowIfCancelled();
            string folder = ws.SnapshotDir(client.Region, version);
            bool fresh = !Directory.Exists(folder);
            try
            {
                string p = Thaliak.SnapshotExe(ws, client.Region, version);
                if (pattern.Scan(Target(p)).Count > 0) return p;
                Log.Note($"[repair] {client.Name} {version} exe 에서도 원래 패턴이 0건 - 더 앞 버전 시도");
                // 직전 출시 버전 exe 는 다른 항목에도 쓰이므로 남기고, 거슬러 올라가며 받은 것만 지운다
                if (fresh && version != previous)
                {
                    _targets.Remove(p);
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                Log.Note($"[repair] {client.Name} 옛 exe({version})를 받지 못함: {ex.Message}");
                failures.Add(ex);
            }
        }
        return null;
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
    private static string? Validate(string pattern, List<GameClient> clients, string kind, string? variant, string itemName,
                                    IReadOnlyList<Sigcheck.Row> original, int? expected)
    {
        var sig = SigPattern.Parse(pattern);
        var parts = new List<string>();
        var brokenCounts = new HashSet<int>();
        foreach (var client in clients)
        {
            if (variant != null && !Sigcheck.Applies(kind, variant, client.Region)) continue;
            var exe = Target(client.ExePath);
            var got = Points(sig, exe, kind);
            if (got.Count == 0) return null;
            var reference = original.Where(r => r.Item.Name == itemName)
                .Select(r => Points(r.Pattern, exe, kind)).FirstOrDefault(p => p.Count > 0);
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
                            list.Add(new Candidate(name, m.Groups[2].Value, label));
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
