using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace ActPluginRepack.Tests;

// 받지 못한 까닭 구분: 연결 실패 / 서버 오류 응답 / 거절 / 막힘. 화면이 서버 오류를 "인터넷 연결을 확인하세요"라고 하지 않고,
// 새 작업에서는 조금 전 실패 기억을 잊는지. Http 의 프로세스 전역 상태를 쓰므로 http 묶음에서 차례로 돈다.
[Collection("http")]
public sealed class NetworkFailureTests : IDisposable
{
    private readonly string _oldCacheDir = HttpCache.Dir;
    private readonly TempDir _cache = new();
    private readonly HashSet<int> _deadPorts = [];
    private int _deadConnects;

    public NetworkFailureTests()
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

    // 죽은 포트는 바로 거부 (진짜로 연결하면 Windows 루프백은 거부까지 약 2초)
    private ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken token)
    {
        lock (_deadPorts)
            if (_deadPorts.Contains(context.DnsEndPoint.Port))
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

    private string DeadUrl()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        lock (_deadPorts) _deadPorts.Add(port);
        return $"http://127.0.0.1:{port}/x";
    }

    // 정해 둔 응답을 차례로 (다 쓰면 500)
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

    private static string Response(int status, string body) =>
        $"HTTP/1.1 {status} X\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    // 서버 오류(5xx)가 계속되면: 서버 오류로 알리고(연결 실패 아님) 잠시 건너뛰며, 건너뛸 때도 인터넷 탓으로 안내하지 않는다
    [Fact]
    public void ServerErrorIsNotAConnectionFailure()
    {
        using var server = new FakeServer(Response(500, ""), Response(502, ""), Response(503, ""), Response(200, "ok"));
        var ex = Assert.Throws<NetworkException>(() => Http.GetStringOrNull(server.Url));
        Assert.Equal(NetworkFailure.ServerError, ex.Kind);
        Assert.Equal(3, server.Requests);
        Assert.Equal(server.Url, ex.Url);
        Assert.Equal(503, ex.Status);
        Assert.Equal("127.0.0.1", ex.Host);

        var skipped = Assert.Throws<NetworkException>(() => Http.GetStringOrNull(server.Url)); // 잠시 건너뜀
        Assert.Equal(NetworkFailure.ServerError, skipped.Kind);
        Assert.DoesNotContain("인터넷 연결", skipped.Message);
        Assert.Equal(3, server.Requests);

        Http.ForgetConnectionFailures(); // 새 작업이면 다시 시도
        Assert.Equal("ok", Http.GetStringOrNull(server.Url));
    }

    // 다시 해도 같은 4xx 는 "거절"로 따로 알리고(서버 일시 오류처럼 "잠시 후 다시"라고 하지 않게) 건너뛰지도 않는다
    [Theory]
    [InlineData(403)]
    [InlineData(400)]
    [InlineData(407)]
    [InlineData(410)]
    [InlineData(451)]
    public void OtherClientErrorIsRefused(int status)
    {
        using var server = new FakeServer(Response(status, ""), Response(200, "ok"));
        var ex = Assert.Throws<NetworkException>(() => Http.GetStringOrNull(server.Url));
        Assert.Equal(NetworkFailure.Refused, ex.Kind);
        Assert.Equal(status, ex.Status);
        Assert.Contains("다시 해도 같습니다", ex.Message);
        Assert.Equal(1, server.Requests);
        Assert.Equal("ok", Http.GetStringOrNull(server.Url)); // 건너뛰지 않음
        // 받지 못한 것으로는 센다 ("아직 공개 전"이 아님)
        Assert.Same(ex, Errors.DownloadFailure([new ToolException("형식"), ex]));
    }

    [Fact]
    public void ConnectionFailureIsReportedAsSuch()
    {
        var ex = Assert.Throws<NetworkException>(() => Http.GetStringOrNull(DeadUrl()));
        Assert.Equal(NetworkFailure.Connection, ex.Kind);
    }

    // 알릴 까닭 고르기: 막힘 > 한도 소진 > 연결 실패 > 서버 오류·거절. 네트워크와 상관없는 실패만이면 null (아직 공개 전)
    [Fact]
    public void DownloadFailurePicksTheMostActionableCause()
    {
        var server = new NetworkException("s", NetworkFailure.ServerError);
        var connection = new NetworkException("c");
        var throttled = new ThrottledException("t", "github.com", null);
        var limit = new GitHubLimitException("l", null, false);
        Assert.Null(Errors.DownloadFailure([new ToolException("형식"), new IOException("파일")]));
        Assert.Same(connection, Errors.DownloadFailure([server, connection]));
        Assert.Same(limit, Errors.DownloadFailure([server, connection, limit]));
        Assert.Same(throttled, Errors.DownloadFailure([limit, throttled, connection]));
    }

    // 단계 설명: 예외 원문("…서버가 오류로 응답했습니다 (HTTP 503).")을 되풀이하지 않고 주소와 상태만, 거절과 토큰 거절은 따로
    [Fact]
    public void OpcodeFailureTextDoesNotRepeatTheMessage()
    {
        var server = new NetworkException("https://x/y 에서 받지 못했습니다. 서버가 오류로 응답했습니다 (HTTP 503).",
                                          NetworkFailure.ServerError, "https://x/y", 503);
        string text = Prepare.OpcodeFailureText(server);
        Assert.DoesNotContain(server.Message, text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "오류로 응답"));
        Assert.EndsWith("(주소: https://x/y, HTTP 503)", text);

        string refused = Prepare.OpcodeFailureText(new NetworkException("r", NetworkFailure.Refused, "https://x/y", 403));
        Assert.Contains("거절", refused);
        Assert.DoesNotContain("오류로 응답", refused);

        Assert.Contains("아직 공개되지 않았을", Prepare.OpcodeFailureText(null));
        Assert.Equal("", new NetworkException("c").Detail);
    }

    // 토큰을 거절당하면 출처에 맞게: 환경 변수가 저장한 토큰보다 먼저 쓰이므로 "설정에서 지우세요"는 소용없다
    [Fact]
    public void TokenRejectedAdviceFollowsTheTokenSource()
    {
        Assert.Contains("환경 변수 GITHUB_TOKEN 을 고치거나 지우세요", GitHub.TokenRejectedAdvice(GitHub.EnvSource));
        Assert.Contains("gh auth login", GitHub.TokenRejectedAdvice(GitHub.GhSource));
        Assert.Contains("설정 화면에서 토큰을 지우거나", GitHub.TokenRejectedAdvice(GitHub.SavedSource, "설정 화면에서 "));
        Assert.DoesNotContain("CLI", GitHub.TokenRejectedAdvice(GitHub.SavedSource));
        try
        {
            GitHub.TokenOverride = () => ("bad", GitHub.EnvSource);
            string text = Prepare.OpcodeFailureText(new NetworkException("t", NetworkFailure.TokenRejected));
            Assert.Contains("GITHUB_TOKEN", text);
            Assert.DoesNotContain("오류로 응답", text);
        }
        finally { GitHub.TokenOverride = null; }
    }

    [Theory]
    [InlineData("github.com", "GitHub 이")]
    [InlineData("api.github.com", "GitHub 이")]
    [InlineData("raw.githubusercontent.com", "GitHub 이")]
    [InlineData("thaliak.xiv.dev", "서버(thaliak.xiv.dev)가")]
    [InlineData("127.0.0.1:1234", "서버(127.0.0.1:1234)가")]
    [InlineData(null, "서버가")]
    public void ServerSubjectNamesGitHubOnlyForGitHub(string? host, string expected) => Assert.Equal(expected, Http.ServerSubject(host));

    // 패치 파일을 받다 끊기면 연결 실패 (형식 오류가 아니라)
    [Fact]
    public void TruncatedPatchDownloadIsAConnectionFailure()
    {
        byte[] magic = [0x91, 0x5A, 0x49, 0x50, 0x41, 0x54, 0x43, 0x48, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n");
        using var server = new FakeServer([.. head, .. magic, 0, 0]);
        var ex = Assert.Throws<NetworkException>(() => Thaliak.ExtractUnverified(server.Url));
        Assert.Equal(NetworkFailure.Connection, ex.Kind);
    }

    // 패치 번호 대응표를 받지 못했으면 그 까닭을 알린다 — 표가 없어 opcodediff 를 못 한 것을 "아직 공개 전"으로 안내하지 않게.
    // 새 작업이 시작되면 잊는다.
    [Fact]
    public void VersionTableFailureIsReportedAsDownloadFailure()
    {
        string oldUrl = GameVersions.Url;
        try
        {
            GameVersions.ResetForTests();
            GameVersions.Url = DeadUrl();
            using var tmp = new TempDir();
            var client = new GameClient("global", Region.Find("global")!, Path.Combine(tmp.Path, "game", "ffxiv_dx11.exe"), "2026.10.06.0000.0000");
            var config = new AppConfig([client], SharpVision: null, Ida: null);
            using var opcodes = new OpcodeCollector(new Workspace(tmp.Path), Path.Combine(tmp.Path, "plugin.dll"), config,
                                                    false, false, false, new HashSet<string> { "opcodediff" });
            Assert.Null(opcodes.Collect(client));
            var failure = Assert.IsType<NetworkException>(opcodes.DownloadFailure);
            Assert.Equal(NetworkFailure.Connection, failure.Kind);
            Assert.Same(failure, GameVersions.LastFailure);

            Http.ForgetConnectionFailures(); // 새 prepare
            Assert.Null(GameVersions.LastFailure);
        }
        finally
        {
            GameVersions.Url = oldUrl;
            GameVersions.ResetForTests();
        }
    }

    // 패치 번호 대응표를 받지 못한 기억(5분)은 새 작업(prepare)이 시작되면 잊는다 — 인터넷을 다시 연결한 뒤 빈 표로 넘어가지 않게
    [Fact]
    public void GameVersionsForgetsFailureOnNewRun()
    {
        string oldUrl = GameVersions.Url;
        try
        {
            GameVersions.ResetForTests();
            GameVersions.Url = DeadUrl();
            Assert.Empty(GameVersions.All);
            int connects = _deadConnects;

            using var server = new FakeServer(Response(200, """[{"retail_version":"7.57","version_string":"2026.10.06.0000.0000"}]"""));
            GameVersions.Url = server.Url;
            Assert.Empty(GameVersions.All);       // 조금 전 실패 — 다시 받지 않음
            Assert.Null(GameVersions.VersionOf("7.57"));
            Assert.Equal(0, server.Requests);
            Assert.Equal(connects, _deadConnects);

            Http.ForgetConnectionFailures();      // 새 prepare
            Assert.Equal("2026.10.06.0000.0000", GameVersions.VersionOf("7.57"));
            Assert.Equal(1, server.Requests);
        }
        finally
        {
            GameVersions.Url = oldUrl;
            GameVersions.ResetForTests();
        }
    }
}

// 공식 패치 서버에서 받은 exe 는 스퀘어 에닉스 서명이 맞아야 쓴다
public sealed class GameExeSignatureTests
{
    [Fact]
    public void UnsignedOrGarbageIsRejected()
    {
        using var tmp = new TempDir();
        string garbage = Path.Combine(tmp.Path, "garbage.exe"), unsigned = Path.Combine(tmp.Path, "ffxiv_dx11.exe");
        File.WriteAllBytes(garbage, "not an exe at all"u8.ToArray());
        File.WriteAllBytes(unsigned, FakePe.Build([0x90, 0xC3]));
        Assert.Throws<ToolException>(() => GameExeSignature.VerifyFile(garbage, "test"));
        Assert.Throws<ToolException>(() => GameExeSignature.VerifyFile(unsigned, "test"));
        // 받아 둔 스냅샷으로 다시 쓰지 않는다 (서명 확인 전에 받은 것, 깨진 것)
        Assert.False(Thaliak.Trusted(garbage));
        Assert.False(Thaliak.Trusted(unsigned));
        Assert.False(Thaliak.Trusted(Path.Combine(tmp.Path, "none.exe")));
    }

    // 작업 폴더의 snapshots\ 에 받아 둔 진짜 게임 exe 가 있으면 통과해야 한다 (없으면 건너뜀).
    // 받는 중인 <exe>.tmp 로도 확인하므로 확장자와 상관없이 통과해야 한다.
    [Fact]
    public void SignedGameExePasses()
    {
        string? exe = FindSnapshotExe();
        if (exe == null) Assert.Skip(@"snapshots\<지역>\<버전>\ffxiv_dx11.exe 없음");
        GameExeSignature.VerifyFile(exe, exe);
        Assert.True(Thaliak.Trusted(exe));
        using var tmp = new TempDir();
        string copy = Path.Combine(tmp.Path, "ffxiv_dx11.exe.tmp");
        File.Copy(exe, copy);
        GameExeSignature.VerifyFile(copy, copy);
    }

    // 시험 실행 폴더에서 위로 올라가며 snapshots\ 를 찾는다
    private static string? FindSnapshotExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string snapshots = Path.Combine(dir.FullName, "snapshots");
            if (!Directory.Exists(snapshots)) continue;
            if (Directory.EnumerateFiles(snapshots, "ffxiv_dx11.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => !f.Contains(@"\ida\", StringComparison.OrdinalIgnoreCase)) is { } found)
                return found;
        }
        return null;
    }
}

// 이번 버전 CSV 를 구하지 못하면 prepare 단계가 경고가 되도록 예외로 알린다 (조용히 지난 패치 이름으로 빌드되지 않게)
public sealed class CsvStaleTests
{
    [Fact]
    public void NoUsableSourceIsReported()
    {
        using var tmp = new TempDir();
        var region = Region.Find("korean")!;
        var client = new GameClient("korean", region, Path.Combine(tmp.Path, "game", "ffxiv_dx11.exe"), "2026.10.06.0000.0000");
        var config = new AppConfig([client], SharpVision: null, Ida: null);
        var csv = new CsvCollector(new Workspace(tmp.Path), config, new HashSet<string>(), overwrite: false); // 출처를 하나도 쓰지 않음

        var ex = Assert.Throws<ToolException>(() => csv.Collect(client));
        Assert.Contains(@"input\csv\", ex.Message);
        var chinese = Assert.Throws<ToolException>(() => csv.CollectChinese(_ => null));
        Assert.Contains(@"input\csv\", chinese.Message);
    }
}
