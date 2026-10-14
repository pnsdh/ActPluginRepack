using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace ActPluginRepack.Tests;

// 다시 시도 / 막힘 / 캐시. 로컬 TCP 서버가 정해 둔 응답을 차례로 돌려준다 (관리자 권한 없이 됨).
// Http·GitHub 는 프로세스 전역 상태(연결 실패한 서버, 오프라인 판정, 조회 기억, 토큰)를 가지므로 다른 시험과 동시에 돌리지 않는다
[CollectionDefinition("http", DisableParallelization = true)]
public sealed class HttpCollection;

[Collection("http")]
public class HttpTests : IDisposable
{
    private readonly string _oldCacheDir = HttpCache.Dir;
    private readonly TempDir _cache = new();

    // 시험마다 처음 상태에서 (앞 시험이 막아 둔 127.0.0.1:<포트> 가 10분 동안 남지 않게), 사용자의 진짜 캐시 대신 임시 폴더.
    // 다시 시도 전 기다림(2초, 5초, Retry-After)을 줄여 시험 시간을 짧게 — 기다림의 순서와 횟수는 그대로.
    public HttpTests()
    {
        Http.ResetForTests();
        Http.BackoffScale = 0.05;
        Http.ConnectOverride = Connect;
        HttpCache.Dir = _cache.Path;
    }

    public void Dispose()
    {
        Http.ConnectOverride = null;
        Http.BackoffScale = 1;
        Http.ResetForTests();
        HttpCache.Dir = _oldCacheDir;
        _cache.Dispose();
    }

    // 아무도 듣지 않는 포트 (DeadUrl) 는 바로 거부한다 — 진짜로 연결하면 Windows 루프백은 SYN 재시도 때문에 거부까지 약 2초
    private static readonly HashSet<int> DeadPorts = [];
    private static int _deadConnects; // 거부한 연결 시도 수

    private static ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken token)
    {
        lock (DeadPorts)
            if (DeadPorts.Contains(context.DnsEndPoint.Port))
            {
                _deadConnects++;
                throw new SocketException((int)SocketError.ConnectionRefused);
            }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.Connect(context.DnsEndPoint.Host, context.DnsEndPoint.Port);
            return ValueTask.FromResult<Stream>(new NetworkStream(socket, ownsSocket: true));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Queue<byte[]> _responses;
        public int Requests;
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/x";

        public FakeServer(params string[] responses) : this(responses.Select(Encoding.UTF8.GetBytes).ToArray()) { }

        public FakeServer(params byte[][] responses)
        {
            _responses = new Queue<byte[]>(responses);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                using (client)
                {
                    var stream = client.GetStream();
                    var buf = new byte[8192];
                    int n = 0;
                    while (!Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n"))
                        n += await stream.ReadAsync(buf.AsMemory(n));
                    Interlocked.Increment(ref Requests);
                    byte[] resp;
                    lock (_responses) resp = _responses.Count > 0 ? _responses.Dequeue() : Encoding.UTF8.GetBytes(Response(500, ""));
                    await stream.WriteAsync(resp);
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private sealed class NullReporter : IReporter
    {
        public static readonly NullReporter Instance = new();
        public void Log(LogKind kind, string message) { }
    }

    private static string Response(int status, string body, string headers = "") =>
        $"HTTP/1.1 {status} X\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{headers}\r\n{body}";

    [Fact]
    public void RetriesServerErrorsAndShortThrottles()
    {
        using var server = new FakeServer(Response(503, ""), Response(429, "", "Retry-After: 1\r\n"), Response(200, "ok"));
        var sw = Stopwatch.StartNew();
        Assert.Equal("ok", Http.GetStringOrNull(server.Url));
        Assert.Equal(3, server.Requests);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1)); // Retry-After 1초도 BackoffScale 로 줄어든다 (원래는 2초 + 1초)
    }

    [Fact]
    public void LongThrottleFailsFastAsThrottled()
    {
        using var server = new FakeServer(Response(429, "", "Retry-After: 600\r\n"));
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<ThrottledException>(() => Http.GetStringOrNull(server.Url));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));          // 10분을 기다리지 않는다
        Assert.Equal(TimeSpan.FromSeconds(600), ex.RetryAfter);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void ThrottledServerIsSkippedAfterwards()
    {
        using var server = new FakeServer(Response(429, "", "Retry-After: 600\r\n"), Response(200, "ok"));
        Assert.Throws<ThrottledException>(() => Http.GetStringOrNull(server.Url));
        Assert.Throws<ThrottledException>(() => Http.GetStringOrNull(server.Url)); // 다시 두드리지 않는다
        Assert.Equal(1, server.Requests);
        Http.ForgetConnectionFailures();                                           // 막힘은 새 작업에서도 유지
        Assert.Throws<ThrottledException>(() => Http.GetStringOrNull(server.Url));
    }

    // 아무도 듣지 않는 포트 (연결은 Connect 가 바로 거부)
    private static string DeadUrl()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        lock (DeadPorts) DeadPorts.Add(port);
        return $"http://127.0.0.1:{port}/x";
    }

    [Fact]
    public void UnreachableServerFailsFastAfterFirstFailure()
    {
        string url = DeadUrl();
        int before = _deadConnects;
        Assert.Throws<NetworkException>(() => Http.GetStringOrNull(url)); // 1번 다시 시도한 끝에 실패
        Assert.Equal(2, _deadConnects - before);
        var sw = Stopwatch.StartNew();
        Assert.Throws<NetworkException>(() => Http.GetStringOrNull(url));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));                // 두 번째는 연결하지 않고 바로
        Assert.Equal(2, _deadConnects - before);
        Http.ForgetConnectionFailures();                                   // 새 작업이면 다시 시도
        Assert.Throws<NetworkException>(() => Http.GetStringOrNull(url));
        Assert.Equal(4, _deadConnects - before);
        using var server = new FakeServer(Response(200, "back"));
        Assert.Equal("back", Http.GetStringOrNull(server.Url));
    }

    // 서로 다른 서버 두 곳에 잇달아 연결하지 못하면 인터넷이 끊긴 것으로 보고 다른 서버도 바로 실패로 넘긴다
    [Fact]
    public void TwoUnreachableServersInARowMeanOffline()
    {
        Http.ForgetConnectionFailures();
        try
        {
            Assert.Throws<NetworkException>(() => Http.GetStringOrNull(DeadUrl()));
            Assert.Throws<NetworkException>(() => Http.GetStringOrNull(DeadUrl()));
            using var server = new FakeServer(Response(200, "ok"));
            var sw = Stopwatch.StartNew();
            Assert.Throws<NetworkException>(() => Http.GetStringOrNull(server.Url));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(0, server.Requests);
            Http.ForgetConnectionFailures();
            Assert.Equal("ok", Http.GetStringOrNull(server.Url));
        }
        finally { Http.ForgetConnectionFailures(); }
    }

    // 그 사이 어느 서버든 응답이 오면 처음부터 다시 센다 (한 서버만 안 되는 것은 인터넷 끊김이 아님)
    [Fact]
    public void ResponseInBetweenResetsOfflineCount()
    {
        Http.ForgetConnectionFailures();
        try
        {
            using var server = new FakeServer(Response(200, "a"), Response(404, ""), Response(200, "b"));
            Assert.Throws<NetworkException>(() => Http.GetStringOrNull(DeadUrl()));
            Assert.Equal("a", Http.GetStringOrNull(server.Url));
            Assert.Throws<NetworkException>(() => Http.GetStringOrNull(DeadUrl()));
            Assert.Null(Http.GetStringOrNull(server.Url));                  // 404 도 응답 — 지운다
            Assert.Throws<NetworkException>(() => Http.GetStringOrNull(DeadUrl()));
            Assert.Equal("b", Http.GetStringOrNull(server.Url));
            Assert.Equal(3, server.Requests);
        }
        finally { Http.ForgetConnectionFailures(); }
    }

    // GitHub 의 여러 주소는 오프라인 판정에서 한 곳으로 센다
    [Theory]
    [InlineData("https://github.com/a/b/releases/latest", "GitHub")]
    [InlineData("https://api.github.com/repos/a/b", "GitHub")]
    [InlineData("https://raw.githubusercontent.com/a/b/main/x.csv", "GitHub")]
    [InlineData("https://objects.githubusercontent.com/x", "GitHub")]
    [InlineData("https://release-assets.githubusercontent.com/x", "GitHub")]
    [InlineData("https://thaliak.xiv.dev/api", "thaliak.xiv.dev")]
    [InlineData("https://notgithub.com/x", "notgithub.com")]
    [InlineData("http://127.0.0.1:1234/x", "127.0.0.1:1234")]
    public void ServerGroups(string url, string group) => Assert.Equal(group, Http.ServerGroup(new Uri(url)));

    private static byte[] BE32(int v) { var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    private static byte[] BE64(long v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
    private static byte[] LE32(int v) => BitConverter.GetBytes(v);

    // ffxiv_dx11.exe 의 SqpkFile(AddFile) 청크 하나로 된 패치 파일의 HTTP 응답. blocks: 블록 머리부터의 바이트
    private static byte[] PatchResponse(long fileOffset, long fileSize, byte[] blocks)
    {
        byte[] path = Encoding.ASCII.GetBytes("game/ffxiv_dx11.exe\0");
        // SqpkFile: [op][2][file_offset][file_size][path_len][2 expansion][2][path] + 블록
        byte[] body = [(byte)'A', 0, 0, .. BE64(fileOffset), .. BE64(fileSize), .. BE32(path.Length), 0, 0, 0, 0, .. path, .. blocks];
        byte[] pre = [.. BE32(body.Length + 5), (byte)'F'];
        uint crc = Crc32.Compute(Crc32.Compute(Crc32.Compute(0, "SQPK"u8), pre), body);
        byte[] patch = [0x91, 0x5A, 0x49, 0x50, 0x41, 0x54, 0x43, 0x48, 0x0D, 0x0A, 0x1A, 0x0A,
                        .. BE32(5 + body.Length), .. "SQPK"u8, .. pre, .. body, .. BE32((int)crc),
                        .. BE32(0), .. "EOF_"u8];
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {patch.Length}\r\nConnection: close\r\n\r\n");
        return [.. head, .. patch];
    }

    // 비압축 블록 하나: [16 머리: header_size, 0, compressed_size(32000 = 비압축), data_size] [데이터] 128 바이트 정렬
    private static byte[] RawBlock(byte[] data)
    {
        byte[] block = [.. LE32(16), .. LE32(0), .. LE32(32000), .. LE32(data.Length), .. data];
        return [.. block, .. new byte[(128 - block.Length % 128) % 128]];
    }

    private static Exception? ExtractFails(byte[] response)
    {
        using var server = new FakeServer(new[] { response });
        var run = Task.Run(() => Thaliak.ExtractUnverified(server.Url));
        Assert.True(((IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "블록 해석이 끝나지 않음");
        return run.Exception?.InnerException;
    }

    // 머리 크기가 0 인 ZiPatch 블록은 제자리를 돌게 만든다 — 멈추지 않고 깨진 파일로 실패해야 한다
    [Fact]
    public void ZiPatchBlockWithZeroHeaderIsRejected()
    {
        Http.ForgetConnectionFailures();
        var ex = Assert.IsType<ToolException>(ExtractFails(PatchResponse(0, 1000, new byte[16])));
        Assert.Contains("블록 머리", ex.Message);
    }

    // 파일 머리가 말하는 크기·위치를 그대로 믿고 메모리를 잡지 않는다 (OutOfMemoryException 은 잡지 않으므로)
    [Fact]
    public void ZiPatchHugeSizesAreRejected()
    {
        Http.ForgetConnectionFailures();
        var size = Assert.IsType<ToolException>(ExtractFails(PatchResponse(0, 1L << 40, RawBlock([1, 2, 3]))));
        Assert.Contains("위치/크기", size.Message);
        var offset = Assert.IsType<ToolException>(ExtractFails(PatchResponse(Thaliak.MaxExeSize + 1, 1000, RawBlock([1, 2, 3]))));
        Assert.Contains("위치/크기", offset.Message);
        // 압축 블록의 data_size 가 한도를 넘어도 풀기 전에 거절한다
        byte[] huge = [.. LE32(16), .. LE32(0), .. LE32(16), .. LE32(int.MaxValue), .. new byte[16]];
        var block = Assert.IsType<ToolException>(ExtractFails(PatchResponse(0, 1000, [.. huge, .. new byte[96]])));
        Assert.Contains("한도", block.Message);
    }

    // 서명을 확인한 뒤에만 남긴다: 서명 없는 exe 는 받은 자리에도, 확인에 쓴 .tmp 로도 남지 않는다
    [Fact]
    public void UnsignedDownloadLeavesNothing()
    {
        Http.ForgetConnectionFailures();
        using var dir = new TempDir();
        string dest = Path.Combine(dir.Path, "ffxiv_dx11.exe");
        byte[] exe = FakePe.Build([0x90, 0xC3]);
        using var server = new FakeServer(new[] { PatchResponse(0, exe.Length, RawBlock(exe)) });
        var ex = Assert.Throws<ToolException>(() => Thaliak.DownloadExe(server.Url, dest));
        Assert.Contains("서명", ex.Message); // 조립은 됐고 서명 확인에서 걸림
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".tmp"));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    // releases/latest 확인용: 넘어가지 않고 Location 만 읽는다
    [Fact]
    public void RedirectIsReturnedWhenNotFollowing()
    {
        Http.ForgetConnectionFailures();
        using var server = new FakeServer(Response(302, "", "Location: /o/r/releases/tag/v1.2\r\n"));
        using var resp = Http.Send(server.Url, redirect: false);
        Assert.NotNull(resp);
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal("/o/r/releases/tag/v1.2", resp.Headers.Location?.OriginalString);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void NotFoundIsNullAndNotRetried()
    {
        using var server = new FakeServer(Response(404, ""));
        Assert.Null(Http.GetStringOrNull(server.Url));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void CancelStopsWaitingForRetry()
    {
        Http.BackoffScale = 1; // 실제 기다림(2초) 도중에 취소되는지 본다
        using var server = new FakeServer(Response(503, ""), Response(503, ""), Response(503, ""));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var op = Operation.Begin(NullReporter.Instance, cts.Token);
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Http.GetStringOrNull(server.Url));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));          // 2초 대기 도중에 끝남
    }

    [Fact]
    public void CacheRoundTripAndCorruption()
    {
        string url = $"https://raw.githubusercontent.com/test/{Guid.NewGuid():N}/file.txt";
        Assert.True(HttpCache.Cacheable(url));
        Assert.False(HttpCache.Cacheable("https://github.com/a/b/releases/download/v1/x.zip"));
        Assert.False(HttpCache.Cacheable("https://api.github.com/rate_limit"));
        Assert.False(HttpCache.Cacheable("https://thaliak.xiv.dev/x"));
        // HTML 이 정상인 곳은 expanded_assets 뿐 — .atom 자리에 온 HTML(가로채기)은 기억하지 않는다
        Assert.True(HttpCache.HtmlExpected("https://github.com/a/b/releases/expanded_assets/v1"));
        Assert.False(HttpCache.HtmlExpected("https://github.com/a/b/commits/main.atom"));
        Assert.False(HttpCache.HtmlExpected("https://github.com/a/b/releases.atom"));
        Assert.False(HttpCache.HtmlExpected("https://raw.githubusercontent.com/a/b/main/x.json"));

        Assert.Null(HttpCache.Get(url));
        HttpCache.Put(url, "hello"u8.ToArray(), "\"etag1\"");
        var hit = HttpCache.Get(url);
        Assert.NotNull(hit);
        Assert.Equal("hello"u8.ToArray(), hit.Body);
        Assert.Equal("\"etag1\"", hit.ETag);
        Assert.True(hit.Fresh);

        HttpCache.Bypass = true;
        try { Assert.Null(HttpCache.Get(url)); }
        finally { HttpCache.Bypass = false; }

        // 잘린 파일은 없는 것으로 (캐시 폴더는 이 시험의 임시 폴더 — 사용자의 진짜 캐시는 건드리지 않는다)
        string file = HttpCache.PathOf(url);
        Assert.StartsWith(_cache.Path, file);
        File.WriteAllBytes(file, File.ReadAllBytes(file)[..^2]);
        Assert.Null(HttpCache.Get(url));
    }
}
