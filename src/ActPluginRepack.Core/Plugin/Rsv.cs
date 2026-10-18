using System.Text;
using System.Xml.Linq;

// RSV(서버가 내려주는 가려진 이름 — 새 레이드 스킬 등)를 ACT 로그에서 모아 input\overrides\global_rsv.txt 로.
//
// OverlayPlugin 은 RSV 를 받으면 ACT 로그에 262 줄로 남긴다:
//   262|시각|언어|값 바이트 수(16진)|_rsv_<id>_..._S<..>_E<..>|이름|해시      (\r \n 은 \\r \\n 로 이스케이프)
// 키에 언어가 들어 있어(_rsv_47842_-1_6_... 의 6 = 한국어) 언어끼리 겹치지 않는다.
//
// 플러그인(FFXIV_ACT_Plugin.Resource.ResourceFile)은 override 파일마다 ToDictionary 로 읽는다 — 한 파일 안에 같은 키가
// 두 줄 있으면 예외가 나서 그 파일과 그 뒤에 읽는 override 파일이 전부 무시된다. 파일끼리 겹치면 나중에 읽은 것이
// 이긴다(추가한 리소스는 맨 뒤). 그래서:
//   - 한 파일 안에는 키를 한 번만 쓴다 (빌드도 확인: Repacker).
//   - 공식 플러그인에 같은 값으로 이미 있는 키는 넣지 않는다 (이미 있는 키는 다음 가져오기 때 정리된다).
//   - 공식과 값이 다른 키는 공식 값을 그대로 두고 알린다.
//   - 이름에 | 가 있거나 줄바꿈이 있는 값(대사 등)은 플러그인이 쓰지 못하거나 쓸 일이 없어 뺀다.
internal sealed record RsvImportResult(
    int Added,                       // 새로 넣은 수
    int AlreadyOfficial,             // 공식 플러그인에 같은 값으로 이미 있어 넣지 않은 수
    int Removed,                     // global_rsv.txt 에 있었지만 공식 플러그인에 들어가 지운 수
    IReadOnlyList<string> Conflicts, // 공식 / 다른 input 파일과 값이 다른 키
    int Total,                       // 가져온 뒤 global_rsv.txt 의 항목 수
    string? FilePath)                // global_rsv.txt (항목이 없으면 null — 파일을 지움)
{
    // 한 줄 요약 (로그·GUI 공통)
    public string Summary =>
        $"새로 {Added}개, 공식 플러그인에 이미 있음 {AlreadyOfficial}개" +
        (Removed > 0 ? $", 공식에 들어가 정리 {Removed}개" : "") +
        (Conflicts.Count > 0 ? $", 값이 달라 넣지 않음 {Conflicts.Count}개" : "") +
        (FilePath != null ? $@" -> input\overrides\{Rsv.FileName} ({Total}개)" : " - 넣을 것 없음");
}

internal static class Rsv
{
    public const string FileName = "global_rsv.txt";
    public const int DefaultDays = 30; // 로그 폴더에서 읽을 기간 (최근 며칠)
    private const string LinePrefix = "262|";

    // ACT 로그 폴더: FFXIV 플러그인 설정 > ACT 설정 > 기본 위치
    public static string LogFolder()
    {
        string config = Path.GetDirectoryName(ActLocator.ActConfigPath)!;
        string? Read(string file, string element, string name)
        {
            try
            {
                string path = Path.Combine(config, file);
                if (!File.Exists(path)) return null;
                return XDocument.Load(path).Descendants(element)
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == name)?.Attribute("Value")?.Value;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException || Errors.IsFileError(ex)) { return null; }
        }
        return new[]
        {
            Read("FFXIV_ACT_Plugin.config.xml", "TextBox", "txtLogFileDirectory"),
            Read("Advanced Combat Tracker.config.xml", "DirectoryInfo", "folderLogs"),
        }.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");
    }

    // 로그 폴더에서 최근 days 일 안에 쓴 로그 (최근 것부터)
    public static List<string> RecentLogs(string folder, int days) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("*.log")
                .Where(f => f.LastWriteTime >= DateTime.Now.AddDays(-days))
                .OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).ToList()
            : [];

    // 262 줄에서 키 -> 이름. 같은 키가 여러 번 나오면 가장 최근 로그의 값(files 는 최근 것부터).
    public static Dictionary<string, string> Scan(IReadOnlyList<string> files, List<string> notes)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            Operation.ThrowIfCancelled();
            // ACT 가 쓰고 있는 로그도 읽을 수 있게
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var inFile = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith(LinePrefix, StringComparison.Ordinal)) continue;
                string[] f = line.Split('|');
                if (f.Length != 7) continue; // 이름에 | 가 있으면 플러그인이 쓰지 못한다
                string key = f[4], value = f[5];
                if (!key.StartsWith("_rsv_", StringComparison.Ordinal) || value.Length == 0) continue;
                if (value.Contains("\\r") || value.Contains("\\n") || key.Contains(' ')) continue;
                inFile[key] = value; // 같은 파일 안에서는 나중 줄
            }
            foreach (var (key, value) in inFile)
            {
                if (!found.TryGetValue(key, out string? newer)) found[key] = value;
                else if (newer != value) notes.Add($"{key}: 로그마다 값이 다릅니다 ('{value}' -> '{newer}'). 최근 값을 씁니다.");
            }
        }
        return found;
    }

    // override 텍스트 -> 키/값 (플러그인과 같은 규칙: 줄마다 | 로 나눠 두 칸인 것만)
    public static List<(string Key, string Value)> Parse(string text) =>
        text.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('|', StringSplitOptions.RemoveEmptyEntries))
            .Where(x => x.Length == 2).Select(x => (x[0], x[1])).ToList();

    // 베이스 플러그인에 들어 있는 override 의 키 -> 값 (파일끼리 겹치면 플러그인처럼 나중 것)
    public static Dictionary<string, string> Official(string pluginPath)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        using var plugin = Repacker.ReadPlugin(pluginPath);
        using var inner = Costura.ReadInner(plugin, Costura.Resource);
        foreach (var r in inner.Resources.OfType<Mono.Cecil.EmbeddedResource>()
                     .Where(r => r.Name.StartsWith(Costura.OverridePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            // 플러그인처럼 StreamReader 로 (BOM 을 떼고) 읽는다 — 공식 파일 중에는 BOM 으로 시작하는 것이 있다
            using var reader = new StreamReader(new MemoryStream(r.GetResourceData()), Encoding.UTF8);
            foreach (var (key, value) in Parse(reader.ReadToEnd())) all[key] = value;
        }
        return all;
    }

    public static RsvImportResult Import(Workspace ws, IReadOnlyList<string> logFiles, string? pluginPath = null)
    {
        pluginPath ??= ws.PluginPath;
        if (!File.Exists(pluginPath)) throw new ToolException($"베이스 플러그인이 없습니다: {pluginPath}. 먼저 prepare 를 실행하세요.");
        return Import(ws, logFiles, Official(pluginPath));
    }

    // official: 베이스 플러그인의 override 키 -> 값
    public static RsvImportResult Import(Workspace ws, IReadOnlyList<string> logFiles, IReadOnlyDictionary<string, string> official)
    {
        var conflicts = new List<string>();
        var notes = new List<string>();
        // 같은 키가 여러 로그에 있으면 가장 최근 로그의 값 — 직접 준 파일 목록도 최근 것부터
        var files = logFiles.OrderByDescending(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue).ToList();
        var found = Scan(files, notes);
        foreach (string n in notes) Log.Note($"[rsv] {n}");
        Log.Info($"[rsv] 로그 {logFiles.Count}개에서 RSV {found.Count}개");

        string target = Path.Combine(ws.OverridesDir, FileName);
        var mine = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(target))
            foreach (var (key, value) in Parse(File.ReadAllText(target))) mine[key] = value; // 예전 파일의 중복은 여기서 하나로
        // 다른 input\overrides 파일 (직접 넣은 것)
        var others = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(ws.OverridesDir))
            // build 가 넣는 파일만 (global_ 로 시작 — Repacker 와 같은 규칙)
            foreach (string f in Directory.GetFiles(ws.OverridesDir, "*.txt")
                         .Where(f => Repacker.IsOverrideFile(Path.GetFileName(f)) &&
                                     !Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase)))
                foreach (var (key, value) in Parse(File.ReadAllText(f))) others[key] = value;

        int added = 0, already = 0, removed = 0;
        foreach (var (key, value) in found)
        {
            if (official.TryGetValue(key, out string? off))
            {
                if (off == value) already++;
                else conflicts.Add($"{key}: 공식 플러그인 값('{off}')과 로그 값('{value}')이 다릅니다. 공식 값을 그대로 둡니다.");
                continue;
            }
            if (others.TryGetValue(key, out string? other))
            {
                if (other != value) conflicts.Add($"{key}: input\\overrides 의 다른 파일 값('{other}')과 로그 값('{value}')이 다릅니다. 그 파일의 값을 그대로 둡니다.");
                continue;
            }
            if (!mine.TryGetValue(key, out string? had) || had != value) added++;
            mine[key] = value;
        }
        // 공식 플러그인이나 다른 파일이 따라잡은 항목은 뺀다 (같은 키를 두 번 정의하지 않게)
        foreach (string key in mine.Keys.Where(k => official.ContainsKey(k) || others.ContainsKey(k)).ToList())
        {
            mine.Remove(key);
            removed++;
        }

        string? written = null;
        if (mine.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("# ActPluginRepack: ACT 로그의 RSV(OverlayPlugin 262 줄)에서 가져옴 - 공식 플러그인에 없는 것만\n");
            foreach (var (key, value) in mine) sb.Append(key).Append('|').Append(value).Append('\n');
            Files.WriteAtomic(target, sb.ToString());
            written = target;
        }
        else if (File.Exists(target)) File.Delete(target); // 빈 override 파일은 빌드가 거부한다

        foreach (string c in conflicts) Log.Warn($"[rsv] {c}");
        var result = new RsvImportResult(added, already, removed, conflicts, mine.Count, written);
        Log.Done($"[rsv] {result.Summary}");
        return result;
    }
}
