// install / restore 에 관리자 권한이 필요하면 이 CLI 를 UAC 로 다시 실행한다 (실제 로직은 Core 의 ElevatedRunner).
internal static class Elevated
{
    public const string LogOption = ElevatedRunner.LogOption;

    public static int Relaunch(string command, IEnumerable<string> args, string reason)
    {
        Log.Warn($"[{command}] {reason} - 관리자 권한으로 다시 실행 (Windows 의 확인 창에서 '예')");
        string exe = Environment.ProcessPath ?? throw new ToolException("실행 파일 경로를 알 수 없습니다.");
        // dotnet ActPluginRepack.dll 로 실행된 경우는 dll 을 넘긴다
        string cli = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "ActPluginRepack.dll") : exe;
        return ElevatedRunner.Run(cli, command, args);
    }

    public static int RunLogged(string? log, Func<int> body) => ElevatedRunner.RunLogged(log, body);
}
