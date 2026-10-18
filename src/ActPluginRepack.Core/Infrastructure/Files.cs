using System.Text;

// 파일 쓰기 도우미
internal static class Files
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // 파일의 SHA256 (소문자 16진수)
    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s));
    }

    // 바이트의 SHA256 (소문자 16진수)
    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));

    // 통째로 바꿔 쓰기: <path>.tmp 에 다 쓴 뒤 이름을 바꾼다 — 도중에 죽어도(창 닫기, 정전) 원래 파일이 반쯤 잘린 채 남지 않는다.
    // 폴더가 없으면 만든다. 글은 기본 UTF-8(BOM 없음).
    public static void WriteAtomic(string path, string text, Encoding? encoding = null) =>
        WriteAtomic(path, (encoding ?? Utf8NoBom).GetBytes(text), encoding?.GetPreamble() ?? []);

    public static void WriteAtomic(string path, byte[] bytes) => WriteAtomic(path, bytes, []);

    // 같은 곳을 가리키는 경로인지 (Windows: 대소문자, 끝의 \, 상대 경로 무시)
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    // 옆에 다 만든 폴더(fresh)로 dest 를 바꾼다: dest 를 비켜 두고(.old) 옮긴 뒤 비켜 둔 것을 지운다. 옮기다 실패하면 비켜 둔 것을
    // 제자리로 돌리고 예외 (그때 fresh 는 그대로 남아 있다 — 부른 쪽이 그 안의 결과를 쓸 수 있게).
    public static void ReplaceDirectory(string fresh, string dest)
    {
        // 비켜 둘 이름은 매번 달리 — 지난번에 못 지운 것이 남아 있어도 막히지 않게 (남은 것은 이번에 다시 지워 본다)
        try
        {
            foreach (string stale in Directory.GetDirectories(Path.GetDirectoryName(Path.GetFullPath(dest))!, Path.GetFileName(dest) + ".old-*"))
                DeleteDirectoryQuietly(stale);
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { }
        string aside = $"{dest}.old-{Guid.NewGuid():N}";
        bool moved = false;
        if (Directory.Exists(dest)) { Directory.Move(dest, aside); moved = true; }
        try { Directory.Move(fresh, dest); }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            if (moved)
                try { Directory.Move(aside, dest); } catch (Exception e) when (Errors.IsFileError(e)) { }
            throw;
        }
        DeleteDirectoryQuietly(aside);
    }

    // 빈 dir 를 만들어 fill 로 채운다 (지난번에 남은 것은 먼저 지운다 — 못 지우면 예외). fill 이 실패하면(확인 실패 포함)
    // dir 를 지우고 예외는 그대로.
    // 빈 폴더로 새로 만든다 — 지난번에 남은 것은 지운다(읽기 전용 포함). 못 지우면 그 까닭을 예외로
    public static void ResetDirectory(string dir)
    {
        if (!DeleteDirectoryQuietly(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    public static void FillDirectory(string dir, Action<string> fill)
    {
        ResetDirectory(dir);
        try { fill(dir); }
        catch
        {
            DeleteDirectoryQuietly(dir);
            throw;
        }
    }

    // 받은 도구·결과 폴더 설치: 옆의 <dest>.tmp 에 fill 로 다 만들고 확인한 뒤(FillDirectory) dest 와 바꾼다(ReplaceDirectory)
    // — 중간에 끊기거나 확인에 실패해도 반쪽 폴더가 "있음"으로 보이지 않게. 실패하면 .tmp 를 지우고 예외는 그대로.
    public static void InstallDirectory(string dest, Action<string> fill)
    {
        string tmp = dest + ".tmp";
        FillDirectory(tmp, fill);
        try { ReplaceDirectory(tmp, dest); }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            DeleteDirectoryQuietly(tmp);
            throw;
        }
    }

    // baseDir 안에서 keep 말고 다른 폴더(이전 버전 등)를 지운다. 못 지운 것은 알리기만 한다 (실패가 아님 — 다음에 다시).
    // tag: 로그 앞머리 ("csv" -> "[csv] ...")
    public static void DeleteOthers(string baseDir, string keep, string tag)
    {
        string[] dirs;
        try { dirs = Directory.Exists(baseDir) ? Directory.GetDirectories(baseDir) : []; }
        catch (Exception ex) when (Errors.IsFileError(ex)) { return; }
        foreach (string dir in dirs.Where(d => !SamePath(d, keep)))
            if (!DeleteDirectoryQuietly(dir)) Log.Note($"[{tag}] 이전 폴더를 지우지 못함 (다음에 다시): {dir}");
    }

    // 폴더를 통째로 지운다 (읽기 전용 파일도). 못 지우면(잠김, 권한) 그냥 둔다. 다 지웠거나 원래 없었으면 true.
    public static bool DeleteDirectoryQuietly(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            // 읽기 전용 파일이 있으면 Delete 가 실패한다 (링크는 따라가지 않는다 — 관리자 권한으로 돌 때 엉뚱한 파일을 건드리지 않게)
            foreach (string f in Directory.GetFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                ClearReadOnly(f);
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { return false; }
    }

    // 파일을 지워 본다. 못 지우면(잠김, 권한) 그냥 둔다. 지웠거나 원래 없었으면 true.
    public static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path); // 없는 파일을 지우는 것은 오류가 아니다
            return true;
        }
        catch (DirectoryNotFoundException) { return true; } // 폴더째 없음
        catch (Exception ex) when (Errors.IsFileError(ex)) { return false; }
    }

    // 읽기 전용이면 푼다 (파일이 없으면 그냥 둔다)
    public static void ClearReadOnly(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    // 쓰다가 프로세스가 죽어 남은 이 파일의 임시 파일 (이름이 매번 달라 덮어쓰이지 않는다) — 한 시간 넘은 것만 지운다
    // (지금 다른 곳이 쓰는 중인 것은 건드리지 않게). 예전 이름(<파일>.tmp)도.
    private static void RemoveStaleTemps(string full)
    {
        try
        {
            string dir = Path.GetDirectoryName(full)!, name = Path.GetFileName(full);
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.EnumerateFiles(dir, name + ".*.tmp").Append(full + ".tmp"))
                if (File.Exists(f) && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1)) File.Delete(f);
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { }
    }

    private static void WriteAtomic(string path, byte[] bytes, byte[] preamble)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // 이름에 프로세스·임의 값을 붙여, 같은 파일을 동시에 쓰는 두 곳(GUI 저장과 CLI detect --save 등)이 서로의 임시 파일을 덮지 않게
        string tmp = $"{full}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        RemoveStaleTemps(full);
        try
        {
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(preamble);
                f.Write(bytes);
                f.Flush(flushToDisk: true); // 이름을 바꾸기 전에 내용이 디스크에 있게
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tmp);
            throw;
        }
    }
}
