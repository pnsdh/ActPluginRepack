// 예외를 나누고 사용자 말로 바꾸는 곳 (예외 규칙은 Exceptions.cs 맨 위)
internal static class Errors
{
    // 한 단계의 실패로 처리할 예외 — 이 밖의 것은 버그이므로 그대로 올린다
    public static bool Recoverable(Exception ex) =>
        ex is ToolException or SignatureException or IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException
            or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException
            or System.ComponentModel.Win32Exception or BadImageFormatException or System.Xml.XmlException;

    // 받은 자료의 형식이 예상과 다를 때 나는 예외 (버그일 수도 있다)
    public static bool IsDataFormat(Exception ex) =>
        ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException;

    // 파일·폴더를 읽거나 쓰지 못함 (잠김, 권한, 없음 등) — catch 필터용
    public static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;

    // 이 프로그램이 알린 실패(메시지가 그대로 보임)이거나 파일을 읽고 쓰지 못함 — 설정을 읽고 저장하는 곳의 catch 필터용
    public static bool IsToolOrFileError(Exception ex) => ex is ToolException || IsFileError(ex);

    // 받다가 실패했는지 (인터넷 / 잠시 막힘 / GitHub 한도) — 화면이 그 원인을 쉬운 말로 안내한다 (Friendly.NetworkCause)
    public static bool IsDownloadFailure(Exception? ex) => ex is NetworkException or ThrottledException or GitHubLimitException;

    // 여러 출처를 받다 난 실패 중 사용자에게 알릴 것 (막힘 > 한도 소진 > 연결 실패 > 서버 오류·거절). 받지 못한 것이 아니면 null
    public static Exception? DownloadFailure(IEnumerable<Exception> failures)
    {
        var list = failures.ToList();
        return list.OfType<ThrottledException>().FirstOrDefault() ??
               list.OfType<GitHubLimitException>().FirstOrDefault() ??
               list.OfType<NetworkException>().OrderBy(n => n.Kind == NetworkFailure.Connection ? 0 : 1).FirstOrDefault() as Exception;
    }

    // 명령줄 프로그램으로 도는지 (CLI 의 Main 이 켠다). CLI 와 화면이 같이 쓰는 문장에서 화면으로는 할 수 없는 안내
    // (옵션 이름, 작업 폴더 안의 경로 등)는 명령줄에서만 붙인다 — 화면은 그 자리에 쉬운 말을 쓴다.
    public static bool CommandLine { get; set; }

    public static string CliOr(string cli, string gui) => CommandLine ? cli : gui;

    public static string CliOnly(string text) => CliOr(text, "");

    // 흔한 예외를 사용자 말로
    public static string Describe(Exception ex) => ex switch
    {
        ToolException or SignatureException => ex.Message,
        OperationCanceledException => "작업을 취소했습니다.",
        UnauthorizedAccessException => $"파일이나 폴더에 접근할 수 없습니다. 다른 프로그램(ACT 등)이 쓰고 있거나 권한이 없을 수 있습니다. ({ex.Message})",
        FileNotFoundException f => $"파일이 없습니다: {f.FileName ?? ex.Message}",
        DirectoryNotFoundException => $"폴더를 찾지 못했습니다. ({ex.Message})",
        IOException => $"파일을 읽거나 쓰지 못했습니다. 다른 프로그램(ACT, 백신 등)이 쓰고 있을 수 있습니다. ({ex.Message})",
        BadImageFormatException => $"올바른 DLL/EXE 파일이 아닙니다. ({ex.Message})",
        _ when IsDataFormat(ex)
            => $"받은 데이터의 형식이 예상과 다릅니다. 원본 저장소의 형식이 바뀌었을 수 있습니다. ({ex.Message})",
        System.ComponentModel.Win32Exception => $"외부 프로그램을 실행하지 못했습니다. 설치되어 있는지, 경로가 맞는지 확인하세요. ({ex.Message})",
        _ => $"예상하지 못한 오류가 생겼습니다. ({ex.GetType().Name}: {ex.Message})",
    };

    // 예상 못 한 예외의 전체 내용을 남길 곳 (시험은 임시 폴더로 바꾼다 — 진짜 %TEMP% 의 기록을 지우지 않게)
    public static string LogPath => LogPathOverride ?? Path.Combine(Path.GetTempPath(), "ActPluginRepack-error.log");
    internal static string? LogPathOverride { get; set; }

    // 이 크기를 넘으면 뒷부분(최근 것)만 반쯤 남긴다
    internal const int MaxLogBytes = 256 * 1024;

    // 한 건의 머리 줄 (잘라 낼 때 이 줄에서 자른다)
    private const string EntryMark = "===== ";

    public static void WriteLog(Exception ex, string context) => WriteLog(ex.ToString(), context);

    // detail: 예외 전체 내용 (관리자 쪽에서 넘겨받은 것 등). 덮어쓰지 않고 이어 쓴다 — 맨 앞에 시각과 context.
    public static void WriteLog(string detail, string context)
    {
        try
        {
            string path = LogPath;
            File.AppendAllText(path, $"{EntryMark}{DateTime.Now:O} {context}\n{detail}\n\n");
            if (new FileInfo(path).Length > MaxLogBytes) KeepTail(path);
        }
        catch (Exception e) when (IsFileError(e)) { }
    }

    // 뒤쪽 절반만 남긴다 (한 건의 머리 줄부터 시작하게)
    private static void KeepTail(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        int start = all.Length - MaxLogBytes / 2;
        byte[] mark = System.Text.Encoding.UTF8.GetBytes("\n" + EntryMark);
        int next = all.AsSpan(start).IndexOf(mark);
        start = next >= 0 ? start + next + 1 : start;
        Files.WriteAtomic(path, all[start..]);
    }
}
