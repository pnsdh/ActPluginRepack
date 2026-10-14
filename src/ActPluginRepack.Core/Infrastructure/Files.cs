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

    // 통째로 바꿔 쓰기: <path>.tmp 에 다 쓴 뒤 이름을 바꾼다 — 도중에 죽어도(창 닫기, 정전) 원래 파일이 반쯤 잘린 채 남지 않는다.
    // 폴더가 없으면 만든다. 글은 기본 UTF-8(BOM 없음).
    public static void WriteAtomic(string path, string text, Encoding? encoding = null) =>
        WriteAtomic(path, (encoding ?? Utf8NoBom).GetBytes(text), encoding?.GetPreamble() ?? []);

    public static void WriteAtomic(string path, byte[] bytes) => WriteAtomic(path, bytes, []);

    private static void WriteAtomic(string path, byte[] bytes, byte[] preamble)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = full + ".tmp";
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
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
