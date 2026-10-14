// "--name value" / "--flag" 만 지원하는 최소 파서. 모르는 옵션은 즉시 에러(오타 방지).
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

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
            }
            else throw new ToolException($"알 수 없는 옵션입니다: '{a}' (쓸 수 있는 것: {string.Join(", ", known)})");
        }
        return o;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Get(string name, string fallback) => _values.GetValueOrDefault(name, fallback);
    public string Need(string name) => Get(name) ?? throw new ToolException($"{name} 옵션이 필요합니다.");
    public bool Flag(string name) => _flags.Contains(name);

    // 콤마 구분 목록 옵션 (예: --filter a,b). 미지정 시 빈 배열.
    public string[] GetList(string name) =>
        Get(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    // --root 가 있으면 그 폴더, 없으면 현재 폴더에서 위로 찾는다
    public Workspace Workspace() => global::Workspace.Find(Get("--root"));
}
