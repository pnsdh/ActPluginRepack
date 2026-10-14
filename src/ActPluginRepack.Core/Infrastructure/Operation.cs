// 진행 상황 보고와 취소. Core 는 화면을 모른다 — 모든 메시지는 Log 로 보내고, 작업을 시작한 쪽(CLI / GUI)이
// Operation.Begin 으로 보고 대상(IReporter)과 취소 토큰을 정한다. AsyncLocal 이라 작업마다 따로 갖는다.
// 아무도 정하지 않았으면 콘솔에 쓴다.

internal enum LogKind
{
    Section, // 단계 제목
    Info,    // 일반 진행
    Note,    // 부가 정보 (흐리게)
    Warn,    // 주의 (계속 진행)
    Done,    // 성공
    Error,   // 실패
}

internal interface IReporter
{
    void Log(LogKind kind, string message);

    // 큰 단계의 진행 (index: 0부터, count: 전체 단계 수). GUI 의 진행 막대용.
    void Progress(string step, int index, int count) { }
}

internal static class Operation
{
    private sealed record Scope(IReporter Reporter, CancellationToken Token);

    private static readonly AsyncLocal<Scope?> Current = new();

    public static IReporter Reporter => Current.Value?.Reporter ?? ConsoleReporter.Instance;
    public static CancellationToken Token => Current.Value?.Token ?? CancellationToken.None;

    public static void ThrowIfCancelled() => Token.ThrowIfCancellationRequested();

    // using var _ = Operation.Begin(reporter, token);
    public static IDisposable Begin(IReporter reporter, CancellationToken token = default)
    {
        var previous = Current.Value;
        Current.Value = new Scope(reporter, token);
        return new Restore(() => Current.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

internal static class Log
{
    public static void Section(string message) => Operation.Reporter.Log(LogKind.Section, message);
    public static void Info(string message = "") => Operation.Reporter.Log(LogKind.Info, message);
    public static void Note(string message) => Operation.Reporter.Log(LogKind.Note, message);
    public static void Warn(string message) => Operation.Reporter.Log(LogKind.Warn, message);
    public static void Done(string message) => Operation.Reporter.Log(LogKind.Done, message);
    public static void Error(string message) => Operation.Reporter.Log(LogKind.Error, message);
    public static void Write(LogKind kind, string message) => Operation.Reporter.Log(kind, message);
    public static void Progress(string step, int index, int count) => Operation.Reporter.Progress(step, index, count);
}

// 색 있는 콘솔 출력 (CLI 기본). Error 는 stderr.
internal sealed class ConsoleReporter : IReporter
{
    public static readonly ConsoleReporter Instance = new();
    private static readonly Lock Gate = new();

    public void Log(LogKind kind, string message)
    {
        lock (Gate)
        {
            if (kind == LogKind.Error)
            {
                Console.Error.WriteLine(message);
                return;
            }
            ConsoleColor? color = kind switch
            {
                LogKind.Section => ConsoleColor.Cyan,
                LogKind.Note => ConsoleColor.DarkGray,
                LogKind.Warn => ConsoleColor.Yellow,
                LogKind.Done => ConsoleColor.Green,
                _ => null,
            };
            if (color != null) Console.ForegroundColor = color.Value;
            Console.WriteLine(message);
            if (color != null) Console.ResetColor();
        }
    }
}
