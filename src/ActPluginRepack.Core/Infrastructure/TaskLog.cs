using System.Text;

// 작업 기록: 작업마다 작업 폴더의 logs\<yyyyMMdd-HHmmss>-<작업 이름>.log 에 화면·콘솔과 같은 줄을 시각과 함께 남긴다.
// 최근 Keep 개만 두고 오래된 것부터 지운다. 문제 보고용 기록 묶음(ReportBundle)이 최근 것을 담는다.
// 기록을 쓰지 못해도(잠김, 디스크 가득 등) 작업은 그대로 한다 — 그 뒤로는 조용히 쓰지 않는다.
internal sealed class TaskLog : IDisposable
{
    public const int Keep = 30;

    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    public string Path { get; }

    private TaskLog(string path, StreamWriter writer) => (Path, _writer) = (path, writer);

    // 새 기록 파일을 연다 (detail: 명령줄 등 맨 위에 적을 것). 열지 못하면 null.
    public static TaskLog? Open(Workspace ws, string name, string? detail = null)
    {
        try
        {
            string dir = ws.LogsDir;
            Directory.CreateDirectory(dir);
            string stem = $"{DateTime.Now:yyyyMMdd-HHmmss}-{SafeName(name)}";
            for (int i = 1; ; i++)
            {
                string path = System.IO.Path.Combine(dir, i == 1 ? $"{stem}.log" : $"{stem}-{i}.log");
                FileStream stream;
                try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) when (i < 100 && File.Exists(path)) { continue; } // 같은 초에 시작한 작업
                var log = new TaskLog(path, new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
                log.Raw($"# ActPluginRepack {AppInfo.DisplayVersion} - {name} - {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
                if (detail != null) log.Raw($"# {detail}");
                log.Raw($"# 작업 폴더: {ws.Root}");
                Prune(dir, Keep);
                return log;
            }
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { return null; }
    }

    // 최근 기록 파일 (새것부터). 이름이 시각으로 시작하므로 이름순이 시작 순서다.
    public static IReadOnlyList<string> Recent(Workspace ws, int count)
    {
        try
        {
            return Directory.Exists(ws.LogsDir)
                ? Directory.GetFiles(ws.LogsDir, "*.log").OrderByDescending(System.IO.Path.GetFileName, StringComparer.Ordinal).Take(count).ToList()
                : [];
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { return []; }
    }

    // keep 개만 남기고 오래된 것부터 지운다 (못 지운 것은 다음에)
    internal static void Prune(string dir, int keep)
    {
        try
        {
            foreach (string old in Directory.GetFiles(dir, "*.log").OrderByDescending(System.IO.Path.GetFileName, StringComparer.Ordinal).Skip(keep))
                Files.TryDeleteFile(old);
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { }
    }

    // 보고 한 줄: "시각 종류 내용" (여러 줄이면 이어지는 줄은 들여 쓴다)
    public void Write(LogKind kind, string message)
    {
        string tag = kind switch
        {
            LogKind.Section => "=====",
            LogKind.Note => "note ",
            LogKind.Warn => "WARN ",
            LogKind.Done => "done ",
            LogKind.Error => "ERROR",
            _ => "     ",
        };
        string prefix = $"{DateTime.Now:HH:mm:ss} {tag} ";
        Raw(prefix + message.ReplaceLineEndings(Environment.NewLine + new string(' ', prefix.Length)));
    }

    // 그대로 한 줄 (머리말, 끝맺음, 예외 전체 내용)
    public void Raw(string line)
    {
        lock (_gate)
        {
            if (_writer == null) return;
            try { _writer.WriteLine(line); }
            catch (Exception ex) when (Errors.IsFileError(ex)) { CloseWriter(); }
        }
    }

    public void Dispose()
    {
        lock (_gate) CloseWriter();
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch (Exception ex) when (Errors.IsFileError(ex)) { }
        _writer = null;
    }

    // 파일 이름에 쓸 수 없는 글자와 공백은 '-' 로
    private static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('-', '.');
        return safe.Length == 0 ? "task" : safe.Length > 40 ? safe[..40] : safe;
    }

    // CLI 의 작업 명령: 보고를 지금 보고 대상(콘솔)과 함께 기록 파일에도. 끝에 종료 코드를(실패면 그 오류도) 적는다.
    // 기록 파일을 열지 못하면 기록 없이 그대로 한다.
    public static int Run(Workspace ws, string name, string? detail, Func<int> body)
    {
        using var log = Open(ws, name, detail);
        if (log == null) return body();
        using var _ = Operation.Begin(new Tee(Operation.Reporter, log), Operation.Token);
        try
        {
            int code = body();
            log.Raw($"# 끝 {DateTime.Now:HH:mm:ss} - 종료 코드 {code}");
            return code;
        }
        catch (Exception ex)
        {
            log.Write(LogKind.Error, $"error: {Errors.Describe(ex)}");
            // 예상 못 한 예외는 전체 내용도 (사용자용 오류·취소는 문장으로 충분)
            if (ex is not (ToolException or SignatureException or OperationCanceledException)) log.Raw(ex.ToString());
            log.Raw($"# 끝 {DateTime.Now:HH:mm:ss} - 종료 코드 {(ex is OperationCanceledException ? ExitCodes.Cancelled : ExitCodes.Of(ex))}");
            throw;
        }
    }

    // 지금 보고 대상에 보내면서 기록 파일에도
    private sealed class Tee(IReporter inner, TaskLog log) : IReporter
    {
        public void Log(LogKind kind, string message)
        {
            inner.Log(kind, message);
            log.Write(kind, message);
        }

        public void Progress(string step, int index, int count) => inner.Progress(step, index, count);
    }
}
