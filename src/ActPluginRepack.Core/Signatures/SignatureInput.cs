// input\signatures\ 의 교체 시그니처: 각 줄 "이름|패턴" (# 주석 가능)
//   deucalion.txt  Recv|E8 $ { ' } 4C 8B 47 10 44 8B 4F 1C        (이름은 sigcheck 가 보여주는 것)
//   memory.txt     ChatLog|48 8b da ...   또는 TraditionalChinese.ChatLog|...   (플러그인 메모리)
//   oodle.txt      OodleFree|48 8b cb ... 또는 Korean.OodleFree|...             (Machina Oodle)
// memory/oodle 은 "변형.이름" 으로 한 변형만, "이름" 만 쓰면 모든 변형에 적용된다.
internal static class SignatureInput
{
    public const string FileName = "deucalion.txt";
    public const string MemoryFile = "memory.txt";
    public const string OodleFile = "oodle.txt";

    public static string FileFor(string kind) => kind switch
    {
        "deucalion" => FileName,
        "memory" => MemoryFile,
        "oodle" => OodleFile,
        _ => throw new ArgumentException(kind),
    };

    public static List<Deucalion.Sig> Load(string? dir, string fileName = FileName)
    {
        if (dir == null) return [];
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return [];
        var result = new List<Deucalion.Sig>();
        int n = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            n++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int bar = line.IndexOf('|');
            if (bar <= 0) throw new ToolException($"{path}:{n}: '이름|패턴' 형식이어야 합니다.");
            string name = line[..bar].Trim(), pattern = line[(bar + 1)..].Trim();
            try { SigPattern.Parse(pattern); }
            catch (ToolException ex) { throw new ToolException($"{path}:{n}: {ex.Message}"); }
            // deucalion(pelite)에서 '*' 는 "절대 주소 포인터 따라가기"라 의미가 다르다 — 플러그인/Machina 표기를 붙여 넣은 실수
            if (fileName == FileName && pattern.Contains('*'))
                throw new ToolException($"{path}:{n}: deucalion 패턴에는 '*' 를 쓸 수 없습니다. 1바이트 와일드카드는 '?' 를 쓰세요.");
            if (result.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"{path}:{n}: '{name}' 시그니처가 두 번 있습니다.");
            result.Add(new Deucalion.Sig(name, pattern));
        }
        return result;
    }

    // 자동 복구가 쓴 줄 바로 위의 출처 주석 ("# auto: deucalion main a7316e7 (2026-10-13) - ...")
    private const string AutoPrefix = "# auto:";
    private static bool IsAutoComment(string line) => line.TrimStart().StartsWith(AutoPrefix, StringComparison.Ordinal);

    // 줄마다 이름과 바로 위의 자동 복구 출처 주석 (직접 쓴 줄이면 null). 형식 검사 없이 읽는다 (배포 설명용).
    public static List<(string Name, string? AutoComment)> LoadWithComments(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return [];
        var result = new List<(string, string?)>();
        string? comment = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (IsAutoComment(line)) { comment = line[AutoPrefix.Length..].Trim(); continue; }
            if (line.Length == 0 || line.StartsWith('#')) { comment = null; continue; }
            int bar = line.IndexOf('|');
            if (bar > 0) result.Add((line[..bar].Trim(), comment));
            comment = null;
        }
        return result;
    }

    // "변형.이름" 은 그 변형만, "이름" 은 모든 변형 (memory/oodle)
    public static IEnumerable<(string Variant, string Name)> ExpandName(string inputName, IEnumerable<string> variants)
    {
        var list = variants.ToList();
        int dot = inputName.IndexOf('.');
        string? variant = dot > 0 ? list.FirstOrDefault(v => v.Equals(inputName[..dot], StringComparison.OrdinalIgnoreCase)) : null;
        return variant != null ? [(variant, inputName[(dot + 1)..])] : list.Select(v => (v, inputName));
    }

    // memory/oodle 입력을 (변형, 이름) 별 하나로: "변형.이름" 줄이 같은 이름의 공통 줄보다 우선한다
    public static List<(string Variant, string Name, string Pattern)> Resolve(IEnumerable<Deucalion.Sig> inputs,
                                                                              IEnumerable<string> variants)
    {
        var list = variants.ToList();
        var map = new Dictionary<(string, string), (string Pattern, bool Specific)>();
        foreach (var s in inputs)
        {
            bool specific = s.Name.IndexOf('.') is > 0 and var dot &&
                            list.Any(v => v.Equals(s.Name[..dot], StringComparison.OrdinalIgnoreCase));
            foreach (var key in ExpandName(s.Name, list))
                if (specific || !map.TryGetValue(key, out var cur) || !cur.Specific)
                    map[key] = (s.Pattern, specific);
        }
        return map.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Pattern)).ToList();
    }

    // 이름의 줄을 바꾸거나 추가한다 (자동 복구가 쓴다). 바로 위에 주석 한 줄을 남긴다.
    public static void Upsert(string dir, string fileName, string name, string pattern, string comment)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        int at = lines.FindIndex(l => l.Trim().Split('|')[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                                      && l.Contains('|') && !l.TrimStart().StartsWith('#'));
        if (at >= 0)
        {
            lines.RemoveAt(at);
            if (at > 0 && IsAutoComment(lines[at - 1])) lines.RemoveAt(--at);
        }
        else at = lines.Count;
        lines.InsertRange(at, [$"{AutoPrefix} {comment}", $"{name}|{pattern}"]);
        WriteLines(path, lines);
    }

    // 공식 플러그인이 따라잡아 필요 없어진 항목의 줄을 정리한다. covers(줄 이름) 가 참인 줄 중 자동 복구가 쓴 줄
    // ("# auto:" 주석이 붙은 것)만 지우고, 사람이 직접 쓴 줄은 이름만 돌려준다 (일부러 넣었을 수 있으므로).
    public static (List<string> Removed, List<string> Manual) PruneAuto(string dir, string fileName, Func<string, bool> covers)
    {
        string path = Path.Combine(dir, fileName);
        var removed = new List<string>();
        var manual = new List<string>();
        if (!File.Exists(path)) return (removed, manual);
        var lines = File.ReadAllLines(path).ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('|')) continue;
            string name = line[..line.IndexOf('|')].Trim();
            if (!covers(name)) continue;
            if (i > 0 && IsAutoComment(lines[i - 1]))
            {
                lines.RemoveRange(i - 1, 2);
                removed.Add(name);
                i--;
            }
            else manual.Add(name);
        }
        if (removed.Count > 0) WriteLines(path, lines);
        return (removed, manual);
    }

    // 사람이 고친 파일이므로 도중에 죽어도 잘리지 않게 통째로 바꿔 쓴다 (File.WriteAllLines 와 같은 형식: UTF-8, 줄마다 줄바꿈)
    private static void WriteLines(string path, IEnumerable<string> lines) =>
        Files.WriteAtomic(path, string.Concat(lines.Select(l => l + Environment.NewLine)));
}
