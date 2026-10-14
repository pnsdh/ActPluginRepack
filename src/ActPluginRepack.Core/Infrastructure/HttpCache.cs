using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// GitHub 에서 받은 파일을 잠깐(3분) 기억한다 — 버튼을 연달아 누르거나 단계만 바꿔 다시 돌려도 같은 요청을
// 되풀이하지 않게 (GitHub 웹 / raw 는 IP 당 제한이 있다). 만료된 것은 ETag 로 바뀌었는지만 묻고(304 면 다시 받지
// 않음 — API 는 304 가 한도도 쓰지 않는다), 바뀌었으면 새로 받는다.
//
// 안전 장치:
//   - 404 와 실패는 기억하지 않는다 — 공개되는 순간 다음 실행에서 바로 보이게
//   - 데이터 자리에 온 HTML(로그인 페이지 가로채기 등)도 기억하지 않는다 — 원래 HTML 인 expanded_assets 만 예외
//   - 릴리스 파일(zip 등 releases/download)과 /rate_limit 은 제외, 32MB 넘는 응답도 제외
//   - 파일 하나에 머리(JSON)+본문을 같이 두고 임시 파일을 거쳐 바꾼다 — GUI 와 CLI 가 동시에 써도 반쪽 파일이 없게
//   - 읽다가 깨졌으면(잘림, 다른 형식) 없는 것으로 보고 새로 받는다. 이틀 넘은 파일은 지운다
//   - 위치는 사용자별(%LOCALAPPDATA%\ActPluginRepack\cache\http) — 작업 폴더와 무관 (시험은 Dir 을 임시 폴더로 바꾼다)
internal static class HttpCache
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);
    private const int MaxBytes = 32 << 20;
    // 시험에서만 바꾼다 (사용자의 진짜 캐시를 건드리지 않게 임시 폴더로)
    internal static string Dir { get; set; } = Path.Combine(AppPaths.UserRoot, "cache", "http");
    private static int _pruned;

    // 이 작업 동안 캐시를 쓰지 않음 (고급 모드 "캐시 무시하고 다시 추출" / --overwrite)
    private static readonly AsyncLocal<bool> BypassFlag = new();
    public static bool Bypass { get => BypassFlag.Value; set => BypassFlag.Value = value; }

    public sealed record Entry(byte[] Body, string? ETag, DateTime SavedAt)
    {
        public bool Fresh => DateTime.UtcNow - SavedAt < FreshFor && SavedAt <= DateTime.UtcNow.AddMinutes(1);
    }

    private sealed record Head(string Url, string? ETag, DateTime SavedAt, int Length);

    public static bool Cacheable(string url) =>
        (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.Ordinal) ||
         url.StartsWith("https://github.com/", StringComparison.Ordinal) ||
         url.StartsWith("https://api.github.com/", StringComparison.Ordinal)) &&
        !url.Contains("/releases/download/", StringComparison.Ordinal) &&
        !url.StartsWith("https://api.github.com/rate_limit", StringComparison.Ordinal);

    // 원래 HTML 로 오는 주소 (릴리스 첨부 파일 목록). 그 밖의 주소에 HTML 이 오면 가로채기 / 오류 페이지로 보고 기억하지 않는다
    // — github.com 의 .atom / .json 자리에 HTML 이 와도 3분 동안 붙잡혀 있지 않게.
    public static bool HtmlExpected(string url) =>
        url.StartsWith("https://github.com/", StringComparison.Ordinal) &&
        url.Contains("/releases/expanded_assets/", StringComparison.Ordinal);

    public static Entry? Get(string url)
    {
        if (Bypass) return null;
        try
        {
            byte[] data = File.ReadAllBytes(PathOf(url));
            int headLength = BitConverter.ToInt32(data, 0);
            var head = JsonSerializer.Deserialize<Head>(data.AsSpan(4, headLength));
            if (head == null || head.Url != url || head.Length != data.Length - 4 - headLength) return null;
            return new Entry(data[(4 + headLength)..], head.ETag, head.SavedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
                                       or IndexOutOfRangeException or NotSupportedException)
        {
            return null; // 없음 / 깨짐 / 다른 프로세스가 바꾸는 중
        }
    }

    public static void Put(string url, byte[] body, string? etag)
    {
        if (body.Length > MaxBytes) return;
        try
        {
            Directory.CreateDirectory(Dir);
            Prune();
            byte[] head = JsonSerializer.SerializeToUtf8Bytes(new Head(url, etag, DateTime.UtcNow, body.Length));
            string path = PathOf(url);
            string tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
            using (var f = File.Create(tmp))
            {
                f.Write(BitConverter.GetBytes(head.Length));
                f.Write(head);
                f.Write(body);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 캐시는 있으면 좋은 것 — 못 써도 그냥 넘어간다 (디스크 가득, 권한, 동시 쓰기)
        }
    }

    // 304: 내용은 그대로, 받은 시각만 새로
    public static void Touch(string url, Entry entry) => Put(url, entry.Body, entry.ETag);

    internal static string PathOf(string url) =>
        Path.Combine(Dir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".bin");

    private static void Prune()
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1) return;
        foreach (var f in new DirectoryInfo(Dir).EnumerateFiles())
        {
            try
            {
                if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(2)) f.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
