// 교차 확인 (게임 데이터): 설치된 클라이언트의 게임 데이터를 쓸 수 있는 수단으로 모두 구해 견준다 — SharpVision, xiv-data-oxidizer
// (설정에 없어도 받아서 빌드 — git·Rust 필요), 이번 버전으로 확인된 공개 저장소. 출처마다 CSV 형식이 조금 달라도 되게 원본이 아니라
// 플러그인에 들어갈 이름 목록(CsvConverter 결과)으로 바꿔 비교한다. 고르는 출처(input\csv)는 그대로 — 보고만.
// 작업 파일은 tools\crosscheck-csv\ (실행마다 새로).
internal static class CsvCrossCheck
{
    // 언어 하나의 결과. Sources: 견준 출처, Files: 견준 이름 목록 파일 수, Differences: 다른 것, Notes: 쓰지 못한 출처와 까닭
    public sealed record Result(string Leaf, List<string> Sources, int Files, List<string> Differences, List<string> Notes);

    // 실제로 고른 것(input\csv) — 새로 구한 것과 같은지만 보고, 독립된 출처로는 세지 않는다
    public const string ChosenLabel = @"input\csv (고른 것)";

    public static List<Result> Run(Workspace ws, AppConfig config, GameClient client, IReadOnlySet<string> sources, bool overwrite)
    {
        var region = client.Region!;
        string work = ws.Tool("crosscheck-csv", region.Name);
        Files.DeleteDirectoryQuietly(work);
        var dirs = region.CsvLeafs.ToDictionary(l => l, _ => new List<(string Label, string Dir)>());
        var notes = region.CsvLeafs.ToDictionary(l => l, _ => new List<string>());
        void NoteAll(string text) { foreach (var n in notes.Values) n.Add(text); }

        // SharpVision (설치된 게임에서)
        if (sources.Contains("sharpvision"))
            try
            {
                string exe = config.SharpVision ?? SharpVisionRelease.Resolve(ws) ?? throw new ToolException("SharpVision 을 받지 못했습니다.");
                string root = SharpVision.CsvRoot(exe, client);
                if (overwrite || SharpVision.MissingSheets(root, region).Count > 0) SharpVision.ExtractCsv(exe, client, overwrite);
                foreach (var (leaf, i) in region.CsvLeafs.Select((l, i) => (l, i)))
                    dirs[leaf].Add(("SharpVision", Path.Combine(root, region.SharpVisionLangs[i])));
            }
            catch (Exception ex) when (Errors.Recoverable(ex)) { NoteAll($"SharpVision: 쓰지 못함 ({Errors.Describe(ex)})"); }

        // xiv-data-oxidizer (설치된 게임에서 — 설정에 없으면 tools\ 에 받아서)
        if (sources.Contains("oxidizer"))
            try
            {
                var tool = Oxidizer.Prepare(ws, config.Oxidizer ?? "true");
                string cache = Oxidizer.ExtractCsv(ws, tool, client, overwrite);
                foreach (string leaf in region.CsvLeafs) dirs[leaf].Add(("xiv-data-oxidizer", Path.Combine(cache, leaf)));
            }
            catch (Exception ex) when (Errors.Recoverable(ex)) { NoteAll($"xiv-data-oxidizer: 쓰지 못함 ({Errors.Describe(ex)})"); }

        // 공개 저장소 (이번 게임 버전으로 확인된 것만)
        foreach (string leaf in region.CsvLeafs)
            foreach (var src in Datamining.For(leaf).Where(s => sources.Contains(s.Name)))
            {
                try
                {
                    var status = Datamining.Check(src, client.Version);
                    if (!status.Usable)
                    {
                        notes[leaf].Add($"{src.Repo}: 이번 버전 것이 아니라 뺌 ({status.Description})");
                        continue;
                    }
                    string dst = Path.Combine(work, "download", leaf, src.Name);
                    Datamining.Download(src, CsvConverter.IsChineseLeaf(leaf) ? CsvConverter.ChineseSheets : CsvConverter.RequiredSheets, dst, status.Sha);
                    dirs[leaf].Add((src.Repo, dst));
                }
                catch (Exception ex) when (Errors.Recoverable(ex)) { notes[leaf].Add($"{src.Repo}: 받지 못함 ({Errors.Describe(ex)})"); }
            }

        // 실제로 고른 것(input\csv — 빌드가 넣는 것)도 함께 — 고른 것이 전에 받은 것 그대로여도 새로 구한 것끼리만 같으면 놓치지 않게
        foreach (string leaf in region.CsvLeafs.Where(l => Directory.Exists(ws.CsvLeafDir(l))))
            dirs[leaf].Insert(0, (ChosenLabel, ws.CsvLeafDir(leaf)));

        var results = new List<Result>();
        foreach (string leaf in region.CsvLeafs)
        {
            Operation.ThrowIfCancelled();
            // 출처마다 플러그인에 들어갈 이름 목록으로 (build 와 같은 목록)
            string[] lists = leaf.Equals("en", StringComparison.OrdinalIgnoreCase) ? [.. CsvConverter.DefaultLists, .. CsvConverter.PinnedLists]
                : CsvConverter.IsChineseLeaf(leaf) ? CsvConverter.ChineseLists : CsvConverter.DefaultLists;
            var converted = new List<(string Label, Dictionary<string, Dictionary<string, string>> Files)>();
            foreach (var (label, dir) in dirs[leaf])
            {
                string outDir = Path.Combine(work, "converted", leaf, converted.Count.ToString());
                try
                {
                    CsvConverter.Convert(dir, outDir, CsvConverter.LangOf(leaf), lists, quiet: true);
                    converted.Add((label, Directory.GetFiles(outDir, "*.txt").ToDictionary(f => Path.GetFileName(f), ReadList)));
                }
                catch (Exception ex) when (Errors.Recoverable(ex)) { notes[leaf].Add($"{label}: 이름 목록으로 바꾸지 못함 ({Errors.Describe(ex)})"); }
            }
            var (files, diffs) = Disagreements(converted);
            results.Add(new Result(leaf, converted.Select(c => c.Label).ToList(), files, diffs, notes[leaf]));
        }
        Files.DeleteDirectoryQuietly(work); // 받은 시트·변환 결과 (보고에 다 담았다)
        return results;
    }

    // "id|이름" 줄 -> id -> 이름 (같은 id 가 또 나오면 첫 것 — 플러그인 로더와 같게)
    private static Dictionary<string, string> ReadList(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            int bar = line.IndexOf('|');
            if (bar > 0) map.TryAdd(line[..bar], line[(bar + 1)..]);
        }
        return map;
    }

    // 출처들의 이름 목록을 파일마다 견준다: 둘 이상에 있는 파일 수와, 다른 것 ("파일: N개 id 다름 (출처들 vs 출처) 예: id '이름' / '이름'").
    // 같은 내용인 출처끼리 묶고, 가장 많은 묶음을 기준으로 나머지가 어디서 다른지 적는다.
    internal static (int Files, List<string> Differences) Disagreements(
        IReadOnlyList<(string Label, Dictionary<string, Dictionary<string, string>> Files)> sources)
    {
        var diffs = new List<string>();
        int compared = 0;
        foreach (string file in sources.SelectMany(s => s.Files.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var have = sources.Where(s => s.Files.ContainsKey(file)).ToList();
            if (have.Count < 2) continue;
            compared++;
            var groups = have.GroupBy(s => Fingerprint(s.Files[file])).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 1) continue;
            var basis = groups[0].First().Files[file];
            string basisLabel = string.Join(", ", groups[0].Select(s => s.Label));
            foreach (var g in groups.Skip(1))
            {
                var other = g.First().Files[file];
                var ids = basis.Keys.Union(other.Keys).Where(id => basis.GetValueOrDefault(id) != other.GetValueOrDefault(id)).ToList();
                string examples = string.Join(", ", ids.Take(3).Select(id => $"{id} '{basis.GetValueOrDefault(id) ?? "(없음)"}' / '{other.GetValueOrDefault(id) ?? "(없음)"}'"));
                diffs.Add($"{file}: {ids.Count}개 id 다름 ({basisLabel} / {string.Join(", ", g.Select(s => s.Label))}) 예: {examples}");
            }
        }
        return (compared, diffs);
    }

    private static string Fingerprint(Dictionary<string, string> list) =>
        Files.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", list.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}|{kv.Value}"))));
}
