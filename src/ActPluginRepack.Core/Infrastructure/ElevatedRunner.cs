using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// 관리자 권한이 필요한 명령(install / restore)을 CLI 로 UAC 를 거쳐 실행한다 (CLI 자신도, GUI 도 이걸 쓴다).
// 관리자 쪽은 창 없이 돌고, 출력은 --log 파일에 (종류, 메시지) 한 줄씩 남긴다 — 끝나면 현재 보고 대상에 그대로 재생한다.
internal static class ElevatedRunner
{
    public const string LogOption = "--log";

    // 관리자 권한으로 다시 실행하기 직전에 보이는 안내 (CLI 와 GUI 가 "<까닭> - " 뒤에 붙인다)
    public const string RelaunchNote = "관리자 권한으로 다시 실행 (Windows 의 확인 창에서 '예')";

    // 프로세스 시작 (시험에서 확인 창의 '아니요' 등을 흉내 낼 때 바꾼다)
    internal static Func<ProcessStartInfo, Process?> Start = Process.Start;

    // cli: ActPluginRepack.exe 또는 .dll(dotnet 으로 실행). 종료 코드를 돌려준다.
    // 확인 창에서 '아니요'면 ElevationCanceledException.
    // 환경 변수 ACTREPACK_NO_ELEVATE=1 이면 확인 창을 띄우지 않고 실패한다 (자동화·시험용 — 사람이 없는 곳에서 창이 떠 멈추지 않게).
    public static int Run(string cli, string command, IEnumerable<string> args)
    {
        _lastError.Value = null;
        if (Environment.GetEnvironmentVariable("ACTREPACK_NO_ELEVATE") == "1")
            throw new ToolException("관리자 권한이 필요하지만 ACTREPACK_NO_ELEVATE=1 이라 확인 창을 띄우지 않았습니다. 아무것도 바꾸지 않았습니다.");
        string log = Path.Combine(Path.GetTempPath(), $"ActPluginRepack-elevated-{Guid.NewGuid():N}.log");
        bool dll = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var all = new List<string>();
        if (dll) all.Add(cli);
        all.Add(command);
        all.AddRange(args);
        all.AddRange([LogOption, log]);

        var psi = new ProcessStartInfo(dll ? "dotnet" : cli)
        {
            UseShellExecute = true, // runas 는 셸 실행에서만
            Verb = "runas",
            Arguments = string.Join(" ", all.Select(Quote)),
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(cli)), // runas 는 기본이 System32 에서 시작
        };
        try
        {
            using var p = Start(psi) ?? throw new ToolException("관리자 권한으로 실행하지 못했습니다.");
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: 확인 창에서 '아니요'
        {
            throw new ElevationCanceledException();
        }
        finally
        {
            Replay(log);
        }
    }

    // 관리자 쪽: 보고를 파일에도 남긴다. 실패도 파일로 남겨야 원래 쪽에서 보인다.
    // 예상 못 한 예외의 전체 내용도 이 파일로 넘겨 원래 쪽이 오류 로그에 쓴다 — 관리자 프로세스가 %TEMP% 의 정해진 이름
    // (Errors.LogPath)에 직접 쓰면, 사용자가 그 자리에 심어 둔 링크를 따라 엉뚱한 파일을 덮어쓸 수 있다.
    public static int RunLogged(string? log, Func<int> body)
    {
        if (log == null) return body();
        using var reporter = new FileReporter(log);
        using var _ = Operation.Begin(reporter, Operation.Token);
        try { return body(); }
        catch (Exception ex)
        {
            reporter.Log(LogKind.Error, $"error: {Errors.Describe(ex)}");
            if (ex is not (ToolException or OperationCanceledException)) reporter.Crash(ex.ToString());
            return ExitCodes.Of(ex);
        }
    }

    // 마지막 Run 에서 관리자 쪽이 남긴 마지막 오류 문장 (실패했을 때 화면이 "관리자 권한으로 실패" 대신 진짜 이유를 보이게)
    private static readonly AsyncLocal<string?> _lastError = new();
    public static string? LastError => _lastError.Value;

    // 관리자 쪽이 남긴 줄을 재생하고 파일을 지운다. 깨진 줄(도중에 끝나 잘린 마지막 줄 등)은 건너뛴다 —
    // 여기서 던지면 finally 안이라 진짜 종료 코드·예외를 가린다.
    private static void Replay(string log)
    {
        try
        {
            if (!File.Exists(log)) return;
            foreach (string line in File.ReadAllLines(log, Encoding.UTF8))
            {
                Entry? entry;
                try { entry = JsonSerializer.Deserialize<Entry>(line); }
                catch (JsonException) { continue; }
                if (entry == null) continue;
                if (entry.Crash != null) Errors.WriteLog(entry.Crash, "elevated");
                else if (entry.Message != null)
                {
                    Operation.Reporter.Log(entry.Kind, entry.Message);
                    if (entry.Kind == LogKind.Error) _lastError.Value = entry.Message.StartsWith("error: ", StringComparison.Ordinal) ? entry.Message[7..] : entry.Message;
                }
            }
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { }
        finally
        {
            Files.TryDeleteFile(log);
        }
    }

    // 한 줄: 보고 (Kind, Message) 또는 예상 못 한 예외의 전체 내용 (Crash)
    private sealed record Entry(LogKind Kind, string? Message, string? Crash = null);

    private sealed class FileReporter(string path) : IReporter, IDisposable
    {
        private readonly StreamWriter _writer = new(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };

        public void Log(LogKind kind, string message) => Write(new Entry(kind, message));

        public void Crash(string detail) => Write(new Entry(LogKind.Error, null, detail));

        private void Write(Entry e)
        {
            lock (_writer) _writer.WriteLine(JsonSerializer.Serialize(e));
        }

        public void Dispose() => _writer.Dispose();
    }

    // Windows 명령줄 인수 규칙대로 따옴표 (공백, 따옴표, 끝 역슬래시)
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
}
