using System.IO.Compression;
using System.Reflection;

// 베이스 플러그인 자동 갱신: ravahn/FFXIV_ACT_Plugin 의 최신 GitHub 릴리스(FFXIV_ACT_Plugin_<버전>.zip)가
// input\plugin 의 것보다 새것이면 받아서 교체한다. 이 도구는 "아직 업데이트되지 않은 현재 플러그인"을
// 고치는 것이므로 베이스는 항상 최신 공식본이어야 한다. 기존 파일은 input\plugin\_backup\<옛 버전>\ 으로.
internal static class PluginRelease
{
    private const string Repo = "ravahn/FFXIV_ACT_Plugin";
    public const string PluginFile = "FFXIV_ACT_Plugin.dll";

    // 최신 릴리스를 확인하지 못해(또는 릴리스 정보를 읽지 못해) 기존 것을 그대로 쓰면 그 이유, 아니면 null (prepare 요약에 경고로).
    // 최신인데 deucalion 배포본(deucalion*.distrib.dll)만 없으면(백신 격리 등) 이유를 돌려주지 않고 공식 릴리스를 다시 받는다.
    public static string? Update(string pluginDir)
    {
        string dll = Path.Combine(pluginDir, PluginFile);
        Version? current = ReadVersion(dll);

        string? tag, url;
        try
        {
            var release = GitHubRelease.Latest(Repo);
            tag = release?.Tag;
            // 정해진 이름(FFXIV_ACT_Plugin_<태그>.zip)을 먼저, 없으면 첫 zip
            url = release?.AssetUrl(n => n.Equals($"FFXIV_ACT_Plugin_{release.Tag}.zip", StringComparison.OrdinalIgnoreCase))
                  ?? release?.AssetUrl(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }
        catch (ToolException ex)
        {
            if (current == null)
            {
                if (ex is NetworkException or ThrottledException or GitHubLimitException) throw; // 원인 그대로 (화면이 쉬운 말로 안내)
                throw new ToolException($"최신 플러그인 릴리스를 확인하지 못했습니다: {ex.Message}");
            }
            Log.Warn($"[plugin] 최신 릴리스 확인 실패 - 기존 {current} 사용 ({ex.Message})");
            return $"최신 릴리스를 확인하지 못해 기존 {current} 버전을 그대로 씁니다.";
        }
        if (tag == null || url == null || !Version.TryParse(tag, out var latest))
        {
            if (current == null) throw new ToolException($"{pluginDir} 에 플러그인이 없고, {Repo} 에도 쓸 수 있는 릴리스가 없습니다.");
            Log.Warn($"[plugin] 최신 릴리스 정보를 읽지 못함 - 기존 {current} 사용");
            return $"최신 릴리스 정보를 읽지 못해 기존 {current} 버전을 그대로 씁니다.";
        }
        if (current != null && current >= latest)
        {
            // 지난번에 백신이 deucalion 배포본만 격리했으면 최신이어도 다시 받는다 (그대로 두면 설치가 계속 거부된다)
            if (Directory.GetFiles(pluginDir, "deucalion*.distrib.dll").Any())
            {
                Log.Note($"[plugin] 베이스 플러그인 {current} (최신)");
                return null;
            }
            Log.Warn(@"[plugin] input\plugin 에 deucalion*.distrib.dll 없음 (백신 격리?) - 공식 릴리스를 다시 받음");
        }

        Log.Note($"[plugin] 베이스 플러그인 {current?.ToString() ?? "(없음)"} -> {latest} 받는 중: {url}");
        byte[] zip = Http.GetBytesOrNull(url) ?? throw new ToolException($"서버에서 찾지 못했습니다: {url}");

        // 임시 폴더에 풀어 검증한 뒤에만 교체한다 — 중간에 끊기거나 Windows Defender 가 오탐으로 격리해도
        // input\plugin 이 반쪽이 되지 않게.
        Directory.CreateDirectory(pluginDir);
        string stage = Path.Combine(pluginDir, ".new");
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        List<string> names;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entries = archive.Entries.Where(e => e.Name.Length > 0).ToList();
            if (entries.All(e => !e.Name.Equals(PluginFile, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"{url} 안에 {PluginFile} 이 없습니다.");
            Directory.CreateDirectory(stage);
            foreach (var e in entries) e.ExtractToFile(Path.Combine(stage, e.Name), overwrite: true);
            Thread.Sleep(500); // 실시간 검사가 끼어들 틈
            foreach (var e in entries)
            {
                string f = Path.Combine(stage, e.Name);
                if (!File.Exists(f) || new FileInfo(f).Length != e.Length) throw new IOException($"{e.Name} 이(가) 사라졌거나 잘렸습니다.");
                using (File.OpenRead(f)) { }
            }
            var staged = ReadVersion(Path.Combine(stage, PluginFile)) ?? throw new IOException($"{PluginFile} 을 읽을 수 없습니다.");
            // 태그와 dll 버전이 다르면 다음 실행에서 또 "새 버전"으로 보고 다시 받게 된다 — 알려만 둔다
            if (Normalize(staged) != Normalize(latest))
                Log.Warn($"[plugin] 릴리스 태그({tag})와 {PluginFile} 의 버전({staged})이 다름 - 받은 파일을 그대로 씀");
            names = entries.Select(e => e.Name).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new ToolException(
                $"베이스 플러그인 {latest} 버전을 받지 못했습니다: {ex.Message}\n" +
                "  Windows Defender 가 오탐으로 격리했을 수 있습니다 (Windows 보안 > 바이러스 및 위협 방지 > 보호 기록).\n" +
                "  작업 폴더를 Defender 제외 목록에 추가한 뒤 다시 시도하세요. (명령줄에서는 직접 받아 input\\plugin 에 넣고 --no-plugin 으로 실행할 수도 있습니다)" +
                (current != null ? $"\n  기존 파일({current})은 그대로 두었습니다." : ""));
        }

        // 검증 통과 -> 기존 파일(새 zip 에 있는 것 + 모든 dll)을 백업으로 옮기고 교체. dll 은 전부 옮긴다 — 새 릴리스에서
        // 빠진 dll(이름이 바뀐 deucalion 배포본 등)이 남아 빌드/설치에 같이 복사되지 않게.
        string? backup = null;
        var existing = names.Select(n => Path.Combine(pluginDir, n))
            .Concat(Directory.GetFiles(pluginDir, "*.dll"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            backup = Path.Combine(pluginDir, "_backup", current?.ToString() ?? "unknown");
            Directory.CreateDirectory(backup);
            foreach (string f in existing) File.Move(f, Path.Combine(backup, Path.GetFileName(f)), overwrite: true);
        }
        foreach (string n in names) File.Move(Path.Combine(stage, n), Path.Combine(pluginDir, n), overwrite: true);
        Directory.Delete(stage, recursive: true);
        Log.Info($@"[plugin] input\plugin <- FFXIV_ACT_Plugin {latest} ({string.Join(", ", names)})" +
                          (backup != null ? $@", 이전 파일은 input\plugin\_backup\{Path.GetFileName(backup)}\" : ""));

        // 공식 릴리스에는 deucalion 배포본이 늘 들어 있다. 옮긴 뒤에 없어졌으면 백신(Windows Defender)이 격리한 것 —
        // 그대로 두면 deucalion 시그니처 없이 빌드/설치된다.
        bool shipped = names.Any(IsDistrib);
        if (shipped) Thread.Sleep(500); // 실시간 검사가 끼어들 틈
        if (!Directory.GetFiles(pluginDir, "deucalion*.distrib.dll").Any())
        {
            if (!shipped)
                Log.Warn($"[plugin] {latest} 릴리스에 deucalion*.distrib.dll 이 없음 - 릴리스 구성이 바뀌었을 수 있음");
            else
                throw new ToolException(
                    $"베이스 플러그인 {latest} 버전의 deucalion 파일(deucalion*.distrib.dll)이 input\\plugin 에서 사라졌습니다.\n" +
                    "  Windows Defender 가 오탐으로 격리했을 수 있습니다 (Windows 보안 > 바이러스 및 위협 방지 > 보호 기록).\n" +
                    "  격리된 파일을 복원하고 작업 폴더를 Defender 제외 목록에 추가한 뒤 다시 시도하세요.");
        }
        return null;
    }

    private static bool IsDistrib(string name) =>
        name.StartsWith("deucalion", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".distrib.dll", StringComparison.OrdinalIgnoreCase);

    // 2/3/4 자리 섞여도 비교되게 빠진 자리는 0 으로
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    // 플러그인 dll 의 어셈블리 버전. 없거나 깨졌으면 null (깨진 파일은 새로 받게 된다).
    private static Version? ReadVersion(string dll)
    {
        if (!File.Exists(dll)) return null;
        try { return AssemblyName.GetAssemblyName(dll).Version; }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"[plugin] {dll} 을 읽을 수 없음 ({ex.Message}) - 새로 받음");
            return null;
        }
    }
}
