// CLI 공통: 옵션 파서, 프로젝트 루트 탐지, 에러 타입, 컬러 출력.

internal sealed class ToolException(string message) : Exception(message);

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
                if (i + 1 >= args.Length) throw new ToolException($"option {a} needs a value");
                o._values[a] = args[++i];
            }
            else throw new ToolException($"unknown option '{a}' (valid: {string.Join(", ", known)})");
        }
        return o;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Get(string name, string fallback) => _values.GetValueOrDefault(name, fallback);
    public bool Flag(string name) => _flags.Contains(name);

    // 콤마 구분 목록 옵션 (예: --filter a,b). 미지정 시 빈 배열.
    public string[] GetList(string name) =>
        Get(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

internal static class ProjectRoot
{
    // --root 미지정 시 cwd 에서 위로 올라가며 build.ps1(프로젝트 마커)을 찾는다.
    public static string Resolve(Options o)
    {
        string? explicitRoot = o.Get("--root");
        if (explicitRoot != null) return Path.GetFullPath(explicitRoot);

        for (string? d = Directory.GetCurrentDirectory(); d != null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "build.ps1")) && Directory.Exists(Path.Combine(d, "input")))
                return d;
        throw new ToolException("project root not found (run inside the project, or pass --root <dir>)");
    }
}

internal static class Ui
{
    public static void Section(string message) => Colored(message, ConsoleColor.Cyan);
    public static void Note(string message) => Colored(message, ConsoleColor.DarkGray);
    public static void Done(string message) => Colored(message, ConsoleColor.Green);

    private static void Colored(string message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
