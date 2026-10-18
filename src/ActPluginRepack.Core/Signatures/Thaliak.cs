using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

// 공식 패치 서버에서 특정 버전의 ffxiv_dx11.exe 받기 (옛 exe — 자동 복구 / 로컬 opcodediff 용).
// Thaliak(https://thaliak.xiv.dev) 이 지역별 패치 파일 주소를 알려주고, 그 ZiPatch 를 내려받으며
// ffxiv_dx11.exe 를 이루는 SqpkFile 블록만 풀어 조립한다. xivdev/opcodediff 의 download_exe.py 를 옮긴 것.
// 패치 파일은 http 로 오므로 조립한 exe 는 스퀘어 에닉스의 서명을 확인한 뒤에만 저장하고, 받아 둔 것도 다시 쓸 때 확인한다 (GameExeSignature).
internal static class Thaliak
{
    // 지역별 기본 게임 패치 저장소
    public static readonly Dictionary<string, string> GameRepo = new(StringComparer.Ordinal)
    {
        [Region.Global.Name] = "4e9a232b", [Region.Korean.Name] = "de199059", [Region.SimplifiedChinese.Name] = "c38effbc",
    };

    // 이 지역의 옛 exe 를 공식 패치 서버에서 받을 수 있는지 (대만판은 안 됨 -> 스냅샷으로 보관)
    public static bool Supports(Region region) => GameRepo.ContainsKey(region.Name);

    private static string RepoOf(Region region) =>
        GameRepo.TryGetValue(region.Name, out string? repo) ? repo : throw new ToolException($"{region.Name} 지역의 Thaliak 저장소가 없습니다.");

    private const string TargetFile = "ffxiv_dx11.exe";
    private const int UncompressedBlock = 32000; // SqpkCompressedBlock: compressed_size 가 이 값이면 비압축
    private static readonly byte[] Magic = [0x91, 0x5A, 0x49, 0x50, 0x41, 0x54, 0x43, 0x48, 0x0D, 0x0A, 0x1A, 0x0A];
    // 조립할 exe 의 최대 크기 (지금 exe 는 50MB 쯤). 패치 파일 머리가 말하는 위치·크기를 그대로 믿고 메모리를 잡지 않게
    // (깨졌거나 바꿔치기한 패치로 메모리가 바닥나면 OutOfMemoryException 은 잡지 않으므로 프로그램이 죽는다)
    internal const long MaxExeSize = 512L << 20;

    // snapshots\<지역>\<버전>\ffxiv_dx11.exe — 온전하고 서명이 맞으면 그대로, 아니면 받아서 저장
    public static string SnapshotExe(Workspace ws, Region region, string version)
    {
        string path = Path.Combine(ws.SnapshotDir(region, version), TargetFile);
        if (File.Exists(path))
        {
            if (Trusted(path)) return path;
            Log.Warn($"[thaliak] {path} 이(가) 깨졌거나 서명이 맞지 않아 다시 받음");
        }
        string url = PatchUrl(region, version);
        Log.Note($"[thaliak] {region.Name} {version} exe 받는 중: {url}");
        byte[] exe = DownloadExe(url, path);
        // exe 안의 빌드 날짜가 요청한 버전 날짜와 다르면 엉뚱한 패치를 받았을 수 있다 (같은 날짜의 다른 패치 등)
        if (BuildDate(exe) is { } built && GameVersion.DatePart(version) is { } date && built != date)
            Log.Warn($"[thaliak] 받은 exe 의 빌드 날짜({built})가 요청한 버전({version})과 다름 - 비교 결과를 확인할 것");
        return path;
    }

    // 지역들 중 이 버전을 가장 먼저 내려준 시각. 한 지역의 패치 목록을 받지 못해도 나머지로 (모두 못 받았을 때만 실패로)
    public static DateTime? FirstReleasedAt(IEnumerable<Region> regions, string version)
    {
        DateTime? first = null;
        Exception? failure = null;
        foreach (var r in regions)
        {
            try
            {
                if (Thaliak.ReleasedAt(r, version) is { } t && (first == null || t < first)) first = t;
            }
            catch (Exception ex) when (Errors.Recoverable(ex)) { failure ??= ex; }
        }
        if (first == null && failure != null) throw failure;
        return first;
    }

    // 이미 받아 둔 snapshots\<지역>\<버전>\ffxiv_dx11.exe 가 온전하고 서명이 맞으면 그 경로, 아니면 null (받지 않음 —
    // 작업 폴더를 잠그지 않고 읽기만 할 때)
    public static string? CachedSnapshotExe(Workspace ws, Region region, string version)
    {
        string path = Path.Combine(ws.SnapshotDir(region, version), TargetFile);
        return Trusted(path) ? path : null;
    }

    // 받아 둔 exe 가 온전하고(PE 섹션까지 다 있음) 스퀘어 에닉스 서명이 맞는지 — 서명을 확인하기 전(예전 버전)에 받은 것,
    // 나중에 깨지거나 바뀐 것을 다시 쓰지 않게. 같은 파일(경로·크기·수정 시각)은 프로세스에서 한 번만 확인한다.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), bool> TrustedFiles = new();

    public static bool Trusted(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        var key = (info.FullName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc);
        if (TrustedFiles.TryGetValue(key, out bool ok)) return ok;
        try
        {
            ok = LooksComplete(File.ReadAllBytes(path));
            if (ok) GameExeSignature.VerifyFile(path, path);
        }
        catch (ToolException ex)
        {
            Log.Note($"[thaliak] 받아 둔 exe 를 쓰지 않음 - {ex.Message}");
            ok = false;
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            Log.Note($"[thaliak] 받아 둔 exe 를 읽지 못해 쓰지 않음 - {ex.Message}");
            return false; // 잠깐 잠긴 것일 수 있으니 기억하지 않는다
        }
        return TrustedFiles[key] = ok;
    }

    // url 의 패치에서 exe 를 받아 dest 에 쓰고 그 내용을 돌려준다. 스퀘어 에닉스의 서명이 맞을 때만 남긴다 (아니면
    // ToolException). 서명은 이름을 바꾸기 전의 <dest>.tmp 로 확인한다 — 임시 폴더에 사본을 또 만들지 않게. 실패하면 .tmp 는 지운다.
    public static byte[] DownloadExe(string url, string dest)
    {
        byte[] exe = ExtractUnverified(url);
        string full = Path.GetFullPath(dest);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = full + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(exe);
                f.Flush(flushToDisk: true);
            }
            GameExeSignature.VerifyFile(tmp, url);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            Files.TryDeleteFile(tmp); // 옮겼으면 없음
        }
        return exe;
    }

    // 게임 버전 -> 그 버전을 만드는 패치 파일 주소. 최근 패치는 이름이 게임 버전과 같지만, 예전 것은
    // 끝자리가 다르다(게임 2025.08.07.0000.0000 <- 패치 2025.08.07.0000.0001). 정확히 없으면 같은 날짜의 마지막 것.
    public static string PatchUrl(Region region, string version)
    {
        var patches = Patches(RepoOf(region));
        return patches.LastOrDefault(p => p.Version == version).Url
               ?? patches.LastOrDefault(p => GameVersion.DatePart(p.Version) is { } d && d == GameVersion.DatePart(version)).Url
               ?? throw new ToolException($"Thaliak 에 {region.Name} 지역 {version} 버전의 패치가 없습니다.");
    }

    // 패치 파일에서 exe 를 받아 조립만 한다 — 서명은 확인하지 않으므로 DownloadExe 를 거쳐서만 쓴다 (시험은 직접)
    internal static byte[] ExtractUnverified(string url)
    {
        try { return Extract(url); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw Http.BodyFailed(url, ex); // 받다 끊김 (연결 끊김, 60초 무응답, 중간에 끝남)
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            throw new ToolException($"패치 파일에서 exe 를 받지 못했습니다 ({url}): {ex.Message}");
        }
    }

    private static byte[] Extract(string url)
    {
        using var resp = Http.Send(url) ?? throw Http.NotFound(url);
        // exe 는 패치 파일 맨 앞에 있어 앞부분(수십 MB)만 받고 멈춘다 (대형 패치의 1.5GB 첫 조각도 34MB 쯤) —
        // 그래도 크기가 들쭉날쭉하니 전체 시간 제한 대신 "60초 동안 데이터 없음"으로 끊는다
        using var s = new BufferedStream(new IdleTimeoutStream(resp.Content.ReadAsStream(), TimeSpan.FromSeconds(60)), 1 << 16);

        if (!ReadExact(s, Magic.Length).AsSpan().SequenceEqual(Magic)) throw new ToolException($"ZiPatch 파일이 아닙니다: {url}");
        ExeFile? exe = null;
        while (true)
        {
            // 청크: [4 크기 BE] [4 종류] [본문] [4 CRC]
            uint rawSize = BinaryPrimitives.ReadUInt32BigEndian(ReadExact(s, 4));
            if (rawSize > 1u << 30) throw new InvalidDataException($"ZiPatch 청크가 너무 큽니다 ({rawSize}).");
            int size = (int)rawSize;
            string type = Encoding.ASCII.GetString(ReadExact(s, 4));
            if (type == "EOF_") break;
            if (type != "SQPK" || size < 5) { Skip(s, size + 4L); continue; }

            byte[] pre = ReadExact(s, 5); // inner_size(4) + command(1)
            if (pre[4] != (byte)'F') { Skip(s, size - 5 + 4L); continue; }
            byte[] body = ReadExact(s, size - 5);
            // 우리가 쓰는 청크는 CRC32(종류 + 본문)로 확인한다 — 전송 중 깨진 exe 를 스냅샷으로 남기지 않게
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(ReadExact(s, 4));
            if (Crc32.Compute(Crc32.Compute(Crc32.Compute(0, Encoding.ASCII.GetBytes(type)), pre), body) != crc)
                throw new InvalidDataException("ZiPatch 청크의 CRC 가 맞지 않습니다 (받은 파일이 손상됨).");
            // 패치는 파일을 순서대로 기록한다 — exe 를 다 받은 뒤 다른 파일이 시작되면 나머지(수백 MB~GB)는 안 받는다
            if (ParseFile(body, ref exe) == FileChunk.Other && exe != null && exe.Complete) break;
        }
        if (exe == null || !exe.Complete) throw new ToolException($"{url} 에 {TargetFile} 가 없거나 일부만 있습니다.");
        return exe.Data.ToArray();
    }

    private enum FileChunk { Exe, Other, Ignored }

    // 조립 중인 exe. Size: SqpkFile 머리의 file_size (전체 크기), Covered: 0 부터 빈틈없이 채운 끝.
    // 헤더의 크기만큼 다 채웠고 PE 섹션도 다 들어 있어야 완성으로 본다 (중간 블록이 빠진 exe 를 스냅샷으로 남기지 않게).
    private sealed class ExeFile
    {
        public readonly MemoryStream Data = new();
        public long Size, Covered;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            Data.Position = offset;
            Data.Write(data);
            if (offset <= Covered) Covered = Math.Max(Covered, offset + data.Length);
        }

        public bool Complete => Covered >= Size && LooksComplete(Data.ToArray());
    }

    // PE 헤더의 섹션 끝까지 다 채워졌는지
    private static bool LooksComplete(byte[] exe)
    {
        try
        {
            var pe = SigLib.Pe(exe, TargetFile);
            return pe.Sections.All(sec => sec.RawOffset + (long)sec.RawSize <= exe.Length);
        }
        catch (ToolException) { return false; }
        catch (ArgumentException) { return false; }
    }

    // SqpkFile 본문: [1 op] [2] [8 file_offset BE] [8 file_size BE] [4 path_len BE] [2 expansion] [2] [path] [블록...]
    private static FileChunk ParseFile(byte[] body, ref ExeFile? exe)
    {
        if (body.Length < 27) return FileChunk.Ignored;
        char op = (char)body[0];
        long fileOffset = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(3));
        long fileSize = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(11));
        int pathLen = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(19));
        if (pathLen < 0 || 27 + pathLen > body.Length) return FileChunk.Ignored;
        string path = Encoding.UTF8.GetString(body, 27, pathLen).TrimEnd('\0');
        if (!path.Equals(TargetFile, StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/" + TargetFile, StringComparison.OrdinalIgnoreCase))
            return op == 'A' && fileOffset == 0 ? FileChunk.Other : FileChunk.Ignored;

        if (op == 'R') { exe = null; return FileChunk.Exe; }   // RemoveAll
        if (op != 'A') return FileChunk.Exe;                    // AddFile 만
        if (fileOffset < 0 || fileSize < 0 || fileOffset > MaxExeSize || fileSize > MaxExeSize)
            throw new InvalidDataException("ZiPatch 파일 머리의 위치/크기가 잘못되었습니다.");
        if (fileOffset == 0) exe = new ExeFile();               // 처음부터 다시 씀
        if (exe == null) return FileChunk.Exe;
        exe.Size = Math.Max(exe.Size, fileSize);

        // 블록: [4 header_size LE] [4] [4 compressed_size LE] [4 data_size LE] ... 데이터, 128 바이트 정렬
        int pos = 27 + pathLen;
        long fileOff = fileOffset;
        while (pos + 16 <= body.Length)
        {
            int start = pos;
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
            int compressed = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos + 8));
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos + 12));
            // 머리 크기가 16 보다 작으면 제자리를 돌 수 있다 (0 이면 영원히) — 깨진 파일로 본다
            if (headerSize < 16 || headerSize > body.Length - start || compressed < 0 || dataSize < 0)
                throw new InvalidDataException("ZiPatch 블록 머리가 잘못되었습니다.");
            if (fileOff + dataSize > MaxExeSize) throw new InvalidDataException("ZiPatch 블록이 exe 크기 한도를 넘습니다.");
            pos = start + headerSize;
            if (compressed != UncompressedBlock)
            {
                if (pos + compressed > body.Length) throw new InvalidDataException("ZiPatch 블록이 잘렸습니다.");
                // 머리가 말한 크기(data_size)까지만 푼다 — 작게 압축된 거대한 데이터로 메모리가 바닥나지 않게.
                // 덜 나오면 나온 만큼만 (빈틈은 완성 확인에서 걸러진다)
                using var ds = new DeflateStream(new MemoryStream(body, pos, compressed), CompressionMode.Decompress);
                byte[] data = new byte[dataSize];
                int got = ds.ReadAtLeast(data, dataSize, throwOnEndOfStream: false);
                exe.Write(fileOff, data.AsSpan(0, got));
                pos += compressed;
            }
            else
            {
                if (pos + dataSize > body.Length) throw new InvalidDataException("ZiPatch 블록이 잘렸습니다.");
                exe.Write(fileOff, body.AsSpan(pos, dataSize));
                pos += dataSize;
            }
            fileOff += dataSize;
            pos = start + ((pos - start + 127) & ~127);
        }
        return FileChunk.Exe;
    }

    // version 보다 앞선 패치들의 버전 (최근 것부터). 패치는 출시 전에 여러 개로 나눠 미리 배포되기도 해서(7.57 의
    // 09.29 / 10.02) 바로 앞 패치가 직전 출시 버전이라는 보장은 없다 — 쓰는 쪽이 내용으로 확인해야 한다.
    public static List<string> EarlierVersions(Region region, string version)
    {
        if (!Supports(region)) return [];
        return Patches(RepoOf(region)).Select(p => p.Version)
            .Where(GameVersion.IsValid)
            .Where(v => string.CompareOrdinal(v, version) < 0)
            .Distinct().OrderByDescending(v => v, StringComparer.Ordinal).ToList();
    }

    // 이 지역의 출시 목록 (게임 버전, 오래된 것부터). 패치는 출시 전에 여러 개로 나눠 미리 만들어 두었다가 출시 때 한꺼번에
    // 내려준다 (7.57: 2026.09.29 / 10.02 / 10.06 을 2026-10-13 에) — 처음 내려준 시각(first_offered)이 같은 패치 묶음이 출시 한 번,
    // 그 안의 가장 큰 버전이 출시 후의 게임 버전이다. 미리 만든 패치나 한국 전용 중간 패치는 묶음 안에 있어 출시로 세지 않는다.
    // 버전 문자열의 날짜는 출시일이 아니다 (SE 의 빌드 날짜로 보임 — 7.57 은 10.06 빌드, 10-13 출시).
    public static List<string> Releases(Region region) =>
        Supports(region) ? ReleasesOf(Patches(RepoOf(region)).Select(p => (p.Version, p.Offered))) : [];

    internal static List<string> ReleasesOf(IEnumerable<(string Version, string? Offered)> patches) =>
        patches.Where(p => GameVersion.IsValid(p.Version))
            .GroupBy(p => p.Offered ?? p.Version) // 시각이 없으면 패치 하나를 출시 하나로
            .Select(g => g.Select(p => p.Version).Max(StringComparer.Ordinal)!)
            .Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();

    // version 바로 앞 출시의 게임 버전 (로컬 opcodediff·시그니처 재생성이 지금 exe 와 맞댈 옛 exe). 없으면 null.
    // version 이 속한 출시(같은 묶음)는 빼므로, 지금 버전이 아직 이 목록에 없어도(Thaliak 이 늦음) 구할 수 있다.
    public static string? PreviousRelease(Region region, string version) => PreviousOf(Releases(region), version);

    internal static string? PreviousOf(IReadOnlyList<string> releases, string version) => EarlierOf(releases, version).FirstOrDefault();

    // version 이 나온 출시를 처음 내려준 시각 (UTC). 이 버전이 아직 목록에 없거나 시각이 없으면 null.
    public static DateTime? ReleasedAt(Region region, string version) =>
        Supports(region) ? ReleasedAtOf(Patches(RepoOf(region)).Select(p => (p.Version, p.Offered)), version) : null;

    internal static DateTime? ReleasedAtOf(IEnumerable<(string Version, string? Offered)> patches, string version) =>
        patches.Where(p => p.Offered != null && GameVersion.BuildPart(p.Version) is { } b && b == GameVersion.BuildPart(version))
            .Select(p => (DateTime?)DateTime.Parse(p.Offered!, System.Globalization.CultureInfo.InvariantCulture,
                                                   System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal))
            .Min();

    // version 앞의 출시들 (가까운 것부터)
    public static List<string> EarlierReleases(Region region, string version) => EarlierOf(Releases(region), version);

    internal static List<string> EarlierOf(IReadOnlyList<string> releases, string version) =>
        releases.Where(r => IsOlder(r, version)).Reverse().ToList();

    // 이 지역의 최신 게임 버전 (공식 패치 서버에 올라온 마지막 패치). Thaliak 이 이 지역을 모르면 null.
    // 출시 전에 미리 배포되는 패치도 출시 때 함께 올라오므로(7.57 의 09.29 / 10.02 / 10.06 — 2025~2026 글로벌 기록에서 출시 전에
    // 먼저 보인 패치는 없었다) 이것이 지금 서버 버전이다. 런처가 지금 내려주는(is_active) 패치만 본다.
    public static string? LatestVersion(Region region) =>
        Supports(region)
            ? Patches(RepoOf(region)).Where(p => p.Active).Select(p => p.Version)
                .Where(GameVersion.IsValid)
                .Max(StringComparer.Ordinal)
            : null;

    // local 이 latest 보다 앞선 버전인지. 끝자리는 비교하지 않는다 — 예전 패치는 게임 버전과 끝자리가 달랐다
    // (게임 2025.08.07.0000.0000 <- 패치 2025.08.07.0000.0001).
    public static bool IsOlder(string local, string latest) =>
        GameVersion.BuildPart(local) is { } a && GameVersion.BuildPart(latest) is { } b && string.CompareOrdinal(a, b) < 0;

    // 패치 파일을 직접 지정해 받는다 (fetch-exe --patch, 백테스트용)
    public static string PatchUrlByName(Region region, string patch) =>
        Patches(RepoOf(region)).Where(p => p.Version == patch.TrimStart('D', 'H')).Select(p => p.Url).LastOrDefault()
        ?? throw new ToolException($"Thaliak 에 {region.Name} 지역의 {patch} 패치가 없습니다.");

    // 저장소의 패치 목록 (버전 문자열, 주소, 지금 내려주는지). 형식이 바뀌었으면 ToolException.
    // 한 작업(prepare — Http.ConnectionEpoch) 동안, 길어도 몇 분은 받은 목록을 다시 쓴다 — 언어·출처마다 출시 시각을 볼 때
    // 패치 당일 바쁜 서버에 같은 목록을 수십 번 묻지 않게. 받지 못한 것은 기억하지 않는다.
    private static readonly Lock PatchesGate = new();
    // (받기 전에 본 작업 번호를 같이 적는다 — 이전 작업에서 시작한 받기가 새 작업이 시작된 뒤에 끝나도 새 작업에서 쓰지 않게)
    private static readonly Dictionary<string, (int Epoch, DateTime At, List<(string Version, string Url, bool Active, string? Offered)> List)> PatchesMemo = [];

    private static List<(string Version, string Url, bool Active, string? Offered)> Patches(string repo)
    {
        int epoch = Http.ConnectionEpoch;
        lock (PatchesGate)
        {
            if (PatchesMemo.TryGetValue(repo, out var m) && m.Epoch == epoch && DateTime.UtcNow - m.At < TimeSpan.FromMinutes(5))
                return m.List;
        }
        var list = FetchPatches(repo);
        lock (PatchesGate) PatchesMemo[repo] = (epoch, DateTime.UtcNow, list);
        return list;
    }

    private static List<(string Version, string Url, bool Active, string? Offered)> FetchPatches(string repo)
    {
        string api = $"https://thaliak.xiv.dev/api/v2beta/repositories/{repo}/patches";
        try
        {
            using var doc = JsonDocument.Parse(Http.GetString(api));
            return doc.RootElement.GetProperty("patches").EnumerateArray()
                .Select(p => (Version: (p.GetProperty("version_string").GetString()
                                        ?? throw new ToolException($"Thaliak 응답에 버전이 없는 패치가 있습니다 ({api}).")).TrimStart('D', 'H'),
                              Url: p.GetProperty("remote_url").GetString()
                                   ?? throw new ToolException($"Thaliak 응답에 주소가 없는 패치가 있습니다 ({api})."),
                              // 지금 런처가 내려주는 패치인지 (없으면 그렇다고 본다)
                              Active: !p.TryGetProperty("is_active", out var a) || a.ValueKind != JsonValueKind.False,
                              // 처음 내려준 시각 (출시 묶음을 가른다 — Releases)
                              Offered: p.TryGetProperty("first_offered", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ToolException($"Thaliak 응답의 형식이 예상과 다릅니다 ({api}): {ex.Message}");
        }
    }

    // exe 의 빌드 문자열 "/*****ff14******rev14101345_2026/10/06_02" 에서 빌드 날짜
    public static string? BuildDate(byte[] exe)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(exe), @"ff14\*+rev\d+_(\d{4})/(\d{2})/(\d{2})");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : null;
    }

    private static byte[] ReadExact(Stream s, int n)
    {
        byte[] buf = new byte[n];
        s.ReadExactly(buf);
        return buf;
    }

    private static void Skip(Stream s, long n)
    {
        byte[] buf = new byte[1 << 16];
        while (n > 0)
        {
            int r = s.Read(buf, 0, (int)Math.Min(buf.Length, n));
            if (r <= 0) throw new EndOfStreamException("ZiPatch 데이터가 중간에 끊겼습니다.");
            n -= r;
        }
    }
}
