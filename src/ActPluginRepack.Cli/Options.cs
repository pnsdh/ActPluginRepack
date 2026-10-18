// "--name value" / "--flag" 만 지원하는 최소 파서. 모르는 옵션은 즉시 에러(오타 방지).
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _all = new(StringComparer.Ordinal); // 여러 번 준 옵션의 값 전부

    public static Options Parse(string[] args, string[] valueOpts, string[]? flagOpts = null)
    {
        var known = valueOpts.Concat(flagOpts ?? []).ToArray();
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (flagOpts?.Contains(a) == true) o._flags.Add(a);
            else if (valueOpts.Contains(a))
            {
                if (i + 1 >= args.Length) throw new ToolException($"{a} 옵션에 값이 필요합니다.");
                o._values[a] = args[++i];
                (o._all.TryGetValue(a, out var all) ? all : o._all[a] = []).Add(args[i]);
            }
            else throw new ToolException($"알 수 없는 옵션입니다: '{a}' (쓸 수 있는 것: {string.Join(", ", known)})");
        }
        return o;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Get(string name, string fallback) => _values.GetValueOrDefault(name, fallback);
    public string Need(string name) => Get(name) ?? throw new ToolException($"{name} 옵션이 필요합니다.");
    public bool Flag(string name) => _flags.Contains(name);
    public IReadOnlyList<string> GetAll(string name) => _all.GetValueOrDefault(name) ?? [];

    // 여러 값: --target 처럼 GetAll 로 읽는 옵션은 여러 번 주고, GetList 로 읽는 옵션은 쉼표로 잇는다 (도움말의 규칙)
    // 쉼표 구분 목록 옵션 (예: --filter a,b). 미지정 시 빈 배열.
    public string[] GetList(string name) =>
        Get(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    // 지역 옵션 (global / korean / ...). fallback 이 없으면 꼭 줘야 한다.
    public global::Region Region(string name, string? fallback = null)
    {
        string value = Get(name) ?? fallback ?? Need(name);
        return global::Region.Find(value)
               ?? throw new ToolException($"알 수 없는 지역입니다: '{value}'. {string.Join(" / ", global::Region.All.Select(r => r.Name))} 중 하나를 쓰세요.");
    }

    // --root 가 있으면 그 폴더, 없으면 현재 폴더에서 위로 찾는다
    public Workspace Workspace() => global::Workspace.Find(Get("--root"));
}
