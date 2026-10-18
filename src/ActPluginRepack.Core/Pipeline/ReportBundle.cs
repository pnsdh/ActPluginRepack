using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

// 문제 보고용 기록 묶음 (GUI 의 [문제 보고용 기록 저장], CLI report). 개발자가 자리에 없을 때 사용자가 버튼 하나로 보낼 수 있게
// 원인을 찾는 데 필요한 것만 zip 하나에 담는다:
//   summary.txt  프로그램·Windows·.NET 버전, 관리자 여부, 작업 폴더, 지금 상태(Status)와 다음에 할 일, 넣지 못한 것
//   logs\        작업 기록(TaskLog) 최근 RecentLogs 개, 오류 기록(error.log)
//   state\       재료·빌드 기록, config.json, gui.json, 업데이트 일지, 최근 백업 기록, 교체 시그니처, opcode·override 파일 목록
//   act.txt      ACT 설정의 플러그인 목록, 설치 대상, FFXIV 플러그인 폴더의 파일 목록(크기·버전·SHA256), OverlayPlugin opcode
// 넣지 않는 것: GitHub 토큰, HTTP 캐시, dll·exe 자체, snapshots, tools. 항목 하나를 넣지 못해도 나머지는 담는다
// (넣지 못한 것은 summary.txt 끝에).
internal static class ReportBundle
{
    public const int RecentLogs = 10, RecentBackups = 5;
    private const long MaxFileBytes = 4 * 1024 * 1024; // 이보다 큰 기록은 뒷부분만
    private const int MaxListedFiles = 300;             // 폴더 하나의 파일 목록 상한

    public static string DefaultName(DateTime at) => $"ActPluginRepack-기록-{at:yyyyMMdd-HHmmss}.zip";

    // zip 을 만들고 담은 항목 이름 목록을 돌려준다. 옆의 임시 파일에 다 쓴 뒤 바꾼다 (도중에 실패해도 반쪽 zip 이 남지 않게).
    public static IReadOnlyList<string> Create(Workspace ws, string zipPath)
    {
        string full = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = $"{full}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var bundle = new Bundle();
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
                bundle.Fill(zip, ws);
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            Files.TryDeleteFile(tmp);
            throw;
        }
        return bundle.Entries;
    }

    private sealed class Bundle
    {
        public List<string> Entries { get; } = [];
        private readonly List<string> _missing = [];
        private ZipArchive _zip = null!;

        public void Fill(ZipArchive zip, Workspace ws)
        {
            _zip = zip;
            var at = DateTime.Now;
            foreach (string log in TaskLog.Recent(ws, RecentLogs)) AddFile($"logs/{Path.GetFileName(log)}", log);
            AddFile("logs/error.log", Errors.LogPath, optional: true);

            AddFile("state/input/.state.json", ws.StatePath, optional: true);
            AddFile($"state/output/{BuildInfo.FileName}", Path.Combine(ws.OutputDir, BuildInfo.FileName), optional: true);
            AddFile("state/config.json", ws.ConfigPath, optional: true);
            AddFile("state/gui.json", AppPaths.GuiSettings, optional: true);
            // 업데이트 일지는 프로그램 폴더에 (CLI 는 그 아래 cli\ 에서 돈다)
            if (UpdateJournal() is { } journal) AddFile($"state/{AppUpdate.JournalFile}", journal);
            Try("백업 기록", () =>
            {
                if (!Directory.Exists(ws.BackupsDir)) return;
                foreach (string dir in Directory.GetDirectories(ws.BackupsDir).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(RecentBackups))
                    AddFile($"state/backups/{Path.GetFileName(dir)}/{BackupManifest.FileName}", Path.Combine(dir, BackupManifest.FileName), optional: true);
            });
            Try("교체 시그니처", () =>
            {
                if (!Directory.Exists(ws.SignaturesDir)) return;
                foreach (string f in Directory.GetFiles(ws.SignaturesDir, "*.txt").Order(StringComparer.OrdinalIgnoreCase))
                    AddFile($"state/signatures/{Path.GetFileName(f)}", f);
            });
            AddText("state/opcodes.txt", "opcode 파일 목록", () => Listing(ws.OpcodesDir, versions: false));
            AddText("state/overrides.txt", "override 파일 목록", () => Listing(ws.OverridesDir, versions: false));
            AddText("act.txt", "ACT 정보", Act);
            // 맨 끝에 (넣지 못한 것까지 적으려고)
            AddText("summary.txt", "요약", () => Summary(ws, at));
        }

        // 한 항목: 실패하면 넣지 못한 것에 적고 넘어간다 (예상 못 한 오류도 — 묶기는 문제가 생겼을 때 쓰는 것이라 멈추지 않는다)
        private void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _missing.Add($"{what} ({Errors.Describe(ex)})"); }
        }

        // 파일을 통째로 읽은 뒤에 항목을 만든다 (읽다 실패해도 반쪽 항목이 남지 않게). optional: 없으면 조용히 건너뜀
        private void AddFile(string entry, string path, bool optional = false) => Try(entry, () =>
        {
            if (optional && !File.Exists(path)) return;
            Add(entry, ReadShared(path));
        });

        private void AddText(string entry, string what, Func<string> text) => Try(what, () => Add(entry, new UTF8Encoding(false).GetBytes(text())));

        private void Add(string entry, byte[] bytes)
        {
            using (var s = _zip.CreateEntry(entry, CompressionLevel.Optimal).Open()) s.Write(bytes);
            Entries.Add(entry);
        }

        private string Summary(Workspace ws, DateTime at)
        {
            var sb = new StringBuilder();
            void Line(string label, string text) => sb.AppendLine(StatusLine.Format(label, text, 14));
            sb.AppendLine("ActPluginRepack 문제 보고용 기록");
            Line("만든 시각", $"{at:yyyy-MM-dd HH:mm:ss zzz}");
            Line("프로그램", $"ActPluginRepack {AppInfo.DisplayVersion} ({Environment.ProcessPath ?? "?"})");
            Line("Windows", $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})");
            Line(".NET", RuntimeInformation.FrameworkDescription);
            Line("64비트", $"Windows {YesNo(Environment.Is64BitOperatingSystem)}, 프로그램 {YesNo(Environment.Is64BitProcess)}");
            Line("관리자 권한", YesNo(Safe(() => Elevation.IsElevated)));
            Line("작업 폴더", ws.Root);
            sb.AppendLine();
            sb.AppendLine("[상태]");
            try
            {
                var s = Status.Collect(ws);
                foreach (var line in s.Describe()) sb.AppendLine("  " + line.Format());
                sb.AppendLine();
                sb.AppendLine("[다음에 할 일]");
                foreach (string a in s.Advice) sb.AppendLine($"  => {a}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sb.AppendLine($"  상태를 읽지 못했습니다: {Errors.Describe(ex)}");
                if (!Errors.Recoverable(ex)) sb.AppendLine(ex.ToString());
            }
            sb.AppendLine();
            sb.AppendLine($"넣지 못한 것: {(_missing.Count > 0 ? string.Join("; ", _missing) : "없음")}");
            return sb.ToString();
        }

        private static string YesNo(bool? b) => b switch { true => "예", false => "아니요", null => "?" };

        private static bool? Safe(Func<bool> f)
        {
            try { return f(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        }

        // ACT 설정의 플러그인 목록, 설치 대상, FFXIV 플러그인 폴더들(해루봇 언어 폴더 포함)의 파일, OverlayPlugin opcode.
        // 부분마다 실패해도 그 자리에 까닭을 적고 계속한다.
        private string Act()
        {
            var sb = new StringBuilder();
            var dirs = new List<string>();
            void AddDir(string? dir)
            {
                if (string.IsNullOrEmpty(dir) || dirs.Any(d => Files.SamePath(d, dir))) return;
                dirs.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)));
            }
            void Part(string what, Action body)
            {
                try { body(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    sb.AppendLine($"  ({what}: {Errors.Describe(ex)})");
                    _missing.Add($"act.txt 의 {what} ({Errors.Describe(ex)})");
                }
            }

            string config = ActLocator.ActConfigPath;
            sb.AppendLine($"ACT 설정 파일: {config}{(File.Exists(config) ? "" : " (없음)")}");
            Part("등록된 플러그인", () =>
            {
                if (!File.Exists(config)) return;
                using var ms = new MemoryStream(ReadShared(config));
                var plugins = XDocument.Load(ms).Descendants("ActPlugins").Elements("Plugin").ToList();
                sb.AppendLine($"등록된 플러그인 ({plugins.Count}개):");
                foreach (var p in plugins)
                {
                    string path = (string?)p.Attribute("Path") ?? "(경로 없음)";
                    sb.AppendLine($"  Enabled={(string?)p.Attribute("Enabled") ?? "?"}  {path}{(File.Exists(path) ? "" : "  (파일 없음)")}");
                    if (Path.GetFileName(path).Equals(PluginRelease.PluginFile, StringComparison.OrdinalIgnoreCase)) AddDir(Path.GetDirectoryName(path));
                }
            });

            sb.AppendLine();
            sb.AppendLine("설치 대상 (ActLocator.ResolveTarget):");
            Part("설치 대상", () =>
            {
                try
                {
                    var t = ActLocator.ResolveTarget(null);
                    sb.AppendLine($"  {t.PluginPath} (Enabled={t.Enabled}, 버전 {t.InstalledVersion ?? "?"})");
                    foreach (string o in t.Others) sb.AppendLine($"  함께: {o}");
                    foreach (string d in t.PluginDirs) AddDir(d);
                }
                catch (ToolException ex)
                {
                    string problem = ex is ActTargetException a ? $"[{a.Problem}] " : "";
                    sb.AppendLine($"  찾지 못함: {problem}{ex.Message}");
                    if (ex is ActTargetException { Paths: { Count: > 0 } paths }) sb.AppendLine($"  켜진 폴더: {string.Join(", ", paths)}");
                }
            });

            // 해루봇: Plugins 아래 언어 폴더 (_CN 도 — 무엇이 깔려 있는지 보려고)
            foreach (string dir in dirs.ToList())
            {
                string? parent = Path.GetDirectoryName(dir);
                if (parent == null || !Path.GetFileName(parent).Equals("Plugins", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string lang in new[] { "FFXIV_ACT_Plugin_KR", "FFXIV_ACT_Plugin_Global", "FFXIV_ACT_Plugin_CN" })
                    if (Directory.Exists(Path.Combine(parent, lang))) AddDir(Path.Combine(parent, lang));
            }

            sb.AppendLine();
            sb.AppendLine("FFXIV 플러그인 폴더:");
            foreach (string dir in dirs)
            {
                sb.AppendLine($"  {dir}");
                Part(dir, () => sb.Append(Listing(dir, versions: true, indent: "    ")));
            }

            sb.AppendLine();
            Part("OverlayPlugin", () =>
            {
                string? resources = ActLocator.ResolveOverlayResources();
                sb.AppendLine($"OverlayPlugin resources: {resources ?? "(찾지 못함)"}");
                if (resources == null) return;
                string opcodes = Path.Combine(resources, Install.OverlayOpcodes);
                sb.AppendLine(File.Exists(opcodes)
                    ? $"  {Install.OverlayOpcodes}  {new FileInfo(opcodes).Length:N0} bytes  {File.GetLastWriteTime(opcodes):yyyy-MM-dd HH:mm:ss}  sha256 {Sha256Shared(opcodes)}"
                    : $"  {Install.OverlayOpcodes} (없음)");
            });
            return sb.ToString();
        }
    }

    // 폴더의 파일 목록 (하위 폴더 포함, 링크는 따라가지 않음): 상대 경로, 크기, 수정 시각, (versions 면 dll·exe 의 버전), SHA256
    private static string Listing(string dir, bool versions, string indent = "")
    {
        if (!Directory.Exists(dir)) return $"{indent}(폴더 없음: {dir})\n";
        var files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
        }).Order(StringComparer.OrdinalIgnoreCase).Take(MaxListedFiles + 1).ToList();
        var sb = new StringBuilder();
        if (files.Count == 0) sb.Append(indent).AppendLine("(빈 폴더)");
        foreach (string f in files.Take(MaxListedFiles))
        {
            string line;
            try
            {
                var info = new FileInfo(f);
                bool binary = f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                string version = versions && binary ? $"  v{VersionOf(f)}" : "";
                line = $"{Path.GetRelativePath(dir, f)}  {info.Length:N0} bytes  {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}{version}  sha256 {Sha256Shared(f)}";
            }
            catch (Exception ex) when (Errors.IsFileError(ex)) { line = $"{Path.GetRelativePath(dir, f)}  (읽지 못함: {ex.Message})"; }
            sb.Append(indent).AppendLine(line);
        }
        if (files.Count > MaxListedFiles) sb.Append(indent).AppendLine($"... ({MaxListedFiles}개까지만)");
        return sb.ToString();
    }

    // 어셈블리 버전 (.NET 이 아니면 파일 버전)
    private static string VersionOf(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "?"; }
        catch (Exception ex) when (ex is BadImageFormatException || Errors.IsFileError(ex))
        {
            try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?"; }
            catch (Exception e) when (Errors.IsFileError(e)) { return "?"; }
        }
    }

    // 다른 프로그램이 쓰는 중인 파일(ACT 설정, 진행 중인 작업 기록)도 읽는다. 너무 크면 뒷부분만.
    private static byte[] ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (s.Length > MaxFileBytes) s.Seek(-MaxFileBytes, SeekOrigin.End);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Sha256Shared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s));
    }

    // 업데이트 일지가 있으면 그 경로 (GUI 는 프로그램 폴더, CLI 는 그 아래 cli\ 에서 돈다)
    private static string? UpdateJournal()
    {
        string app = AppContext.BaseDirectory;
        return new[] { app, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(app)) }
            .OfType<string>().Select(d => Path.Combine(d, AppUpdate.JournalFile)).FirstOrDefault(File.Exists);
    }
}
