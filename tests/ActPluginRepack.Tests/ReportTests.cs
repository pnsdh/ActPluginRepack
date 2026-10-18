using System.IO.Compression;
using System.Text;

namespace ActPluginRepack.Tests;

// 오류 기록 경로(Errors.LogPathOverride)는 프로세스 전역이므로 다른 시험과 동시에 돌리지 않는다
[CollectionDefinition("error-log", DisableParallelization = true)]
public sealed class ErrorLogCollection;

// 문제 보고용 기록 묶음: 기대한 항목이 들어가고, 비밀(토큰)과 큰 바이너리(dll·exe)는 들어가지 않는다
[Collection("act-config")]
public class ReportBundleTests
{
    private const string Secret = "ghp_THIS_IS_A_SECRET_TOKEN_VALUE_123";

    [Fact]
    public void BundleHasExpectedEntriesAndNoSecrets()
    {
        using var tmp = new TempDir();
        string? oldEnv = Environment.GetEnvironmentVariable("ACTREPACK_ACT_CONFIG");
        string? oldLog = Errors.LogPathOverride;
        try
        {
            var ws = new Workspace(tmp.Dir("ws"));
            ws.EnsureCreated();
            tmp.File("ws/input/.state.json", "{ \"state\": 1 }");
            tmp.File("ws/output/.build.json", "{ \"build\": 1 }");
            tmp.File("ws/output/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3", "patched"));
            tmp.File("ws/config.json", "{ \"ida\": false, \"clients\": {} }");
            tmp.File("ws/input/signatures/ffxiv_dx11.txt", "RecvSig = 48 8B ??");
            tmp.File("ws/input/opcodes/Global.txt", "ActorCast|0x123");
            tmp.File("ws/input/overrides/global_rsv.txt", "123|이름");
            tmp.File("ws/input/plugin/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3", "official"));
            tmp.File("ws/snapshots/global/2026.10.06/ffxiv_dx11.exe", "exe");
            tmp.File("ws/tools/python/python.exe", "exe");
            tmp.File("ws/github-token.bin", Secret); // 작업 폴더에 잘못 둔 것이라도 담지 않는다
            for (int i = 0; i < 12; i++) tmp.File($"ws/logs/20261018-1200{i:00}-prepare.log", $"log {i}");
            for (int i = 0; i < 7; i++) tmp.File($"ws/backups/20261018-1200{i:00}/backup.json", $"{{ \"n\": {i} }}");
            tmp.File("ws/backups/20261018-120006/KR/FFXIV_ACT_Plugin.dll", "old dll");
            Errors.LogPathOverride = tmp.File("error.log", "===== 2026-10-18 gui\nboom\n\n");

            // 가짜 ACT: 해루봇 한국·글로벌 폴더 + OverlayPlugin
            string kr = tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.3", "kr"));
            tmp.File("act/Plugins/FFXIV_ACT_Plugin_KR/deucalion-1.2.0.dll", "native");
            tmp.File("act/Plugins/FFXIV_ACT_Plugin_Global/FFXIV_ACT_Plugin.dll", FakeAssembly.WithVersion("FFXIV_ACT_Plugin", "3.0.3.2", "global"));
            string overlay = tmp.File("act/Plugins/OverlayPlugin/OverlayPlugin.dll", "overlay");
            tmp.File("act/Plugins/OverlayPlugin/resources/opcodes.jsonc", "{ }");
            string config = tmp.File("act/Advanced Combat Tracker.config.xml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Config>
                  <ActPlugins>
                    <Plugin Path="{overlay}" Enabled="True" />
                    <Plugin Path="{kr}" Enabled="True" />
                  </ActPlugins>
                </Config>
                """);
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", config);

            string zipPath = Path.Combine(tmp.Path, "out", "report.zip");
            var entries = ReportBundle.Create(ws, zipPath);

            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Equal(names.Order(), entries.Order());
            foreach (string expected in new[]
                     {
                         "summary.txt", "act.txt", "logs/error.log", "state/input/.state.json", "state/output/.build.json",
                         "state/config.json", "state/signatures/ffxiv_dx11.txt", "state/opcodes.txt", "state/overrides.txt",
                         "logs/20261018-120011-prepare.log", "state/backups/20261018-120006/backup.json",
                     })
                Assert.Contains(expected, names);
            Assert.Equal(ReportBundle.RecentLogs, names.Count(n => n.StartsWith("logs/2026", StringComparison.Ordinal)));
            Assert.DoesNotContain("logs/20261018-120001-prepare.log", names); // 오래된 것은 빠짐
            Assert.Equal(ReportBundle.RecentBackups, names.Count(n => n.StartsWith("state/backups/", StringComparison.Ordinal)));

            // 비밀·캐시·바이너리는 없다
            Assert.DoesNotContain(names, n => n.Contains("github-token", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("snapshots") || n.Contains("tools/") || n.Contains("cache"));
            var texts = zip.Entries.ToDictionary(e => e.FullName, Read);
            Assert.DoesNotContain(texts, t => t.Value.Contains(Secret));
            Assert.DoesNotContain(texts, t => t.Value.Contains("patched") || t.Value.Contains("old dll")); // dll 내용 자체가 아님

            // 요약: 작업 폴더와 상태, 넣지 못한 것
            string summary = texts["summary.txt"];
            Assert.Contains(ws.Root, summary);
            Assert.Contains("[상태]", summary);
            Assert.Contains("넣지 못한 것: 없음", summary);
            Assert.Contains(AppInfo.DisplayVersion, summary);

            // ACT: 등록된 플러그인, 해루봇의 다른 언어 폴더, 파일 목록(버전·해시), OverlayPlugin opcode
            string act = texts["act.txt"];
            Assert.Contains(kr, act);
            Assert.Contains("FFXIV_ACT_Plugin_Global", act);
            Assert.Contains("v3.0.3.3", act);
            Assert.Contains("v3.0.3.2", act);
            Assert.Contains("deucalion-1.2.0.dll", act);
            Assert.Contains(Files.Sha256Of(kr), act);
            Assert.Contains("opcodes.jsonc", act);

            // opcode 목록은 이름·크기·해시만
            Assert.Contains(Files.Sha256Of(Path.Combine(ws.OpcodesDir, "Global.txt")), texts["state/opcodes.txt"]);
            Assert.DoesNotContain("ActorCast", texts["state/opcodes.txt"]);

            // 임시 파일은 남지 않는다
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(zipPath)!, "*.tmp"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", oldEnv);
            Errors.LogPathOverride = oldLog;
        }
    }

    [Fact]
    public void MissingItemsDoNotFailTheBundle()
    {
        // 빈 작업 폴더, ACT 설정 없음: 그래도 요약과 ACT 정보는 만든다
        using var tmp = new TempDir();
        string? oldEnv = Environment.GetEnvironmentVariable("ACTREPACK_ACT_CONFIG");
        string? oldLog = Errors.LogPathOverride;
        try
        {
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", Path.Combine(tmp.Path, "no-act.xml"));
            Errors.LogPathOverride = Path.Combine(tmp.Path, "no-error.log");
            var ws = new Workspace(tmp.Dir("ws"));
            string zipPath = Path.Combine(tmp.Path, "r.zip");
            var entries = ReportBundle.Create(ws, zipPath);
            Assert.Contains("summary.txt", entries);
            Assert.Contains("act.txt", entries);
            Assert.DoesNotContain("logs/error.log", entries);
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Contains("(없음)", Read(zip.GetEntry("act.txt")!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTREPACK_ACT_CONFIG", oldEnv);
            Errors.LogPathOverride = oldLog;
        }
    }

    private static string Read(ZipArchiveEntry e)
    {
        using var s = new StreamReader(e.Open(), Encoding.UTF8);
        return s.ReadToEnd();
    }
}

// 작업 기록 파일: 보고를 그대로 옮기고, 최근 Keep 개만 둔다
public class TaskLogTests
{
    private sealed class Capture : IReporter
    {
        public readonly List<string> Lines = [];
        public void Log(LogKind kind, string message) { lock (Lines) Lines.Add(message); }
    }

    [Fact]
    public void KeepsOnlyRecentLogs()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("ws"));
        for (int i = 0; i < TaskLog.Keep + 5; i++) tmp.File($"ws/logs/20200101-0000{i:00}-old.log", "x");
        using (var log = TaskLog.Open(ws, "새 작업"))
        {
            Assert.NotNull(log);
            Assert.StartsWith(DateTime.Now.ToString("yyyyMMdd"), Path.GetFileName(log.Path));
            Assert.EndsWith("-새-작업.log", log.Path);
        }
        var left = Directory.GetFiles(ws.LogsDir, "*.log").Select(Path.GetFileName).ToList();
        Assert.Equal(TaskLog.Keep, left.Count);
        // 오래된 것부터 지웠다 (35 + 새것 1 -> 30: 앞의 6개가 빠짐)
        Assert.DoesNotContain("20200101-000005-old.log", left);
        Assert.Contains("20200101-000006-old.log", left);
        Assert.Equal(TaskLog.Keep, TaskLog.Recent(ws, 100).Count);
    }

    [Fact]
    public void RunWritesReportedLinesAndExitCode()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("ws"));
        var console = new Capture();
        using (Operation.Begin(console, TestContext.Current.CancellationToken))
        {
            int code = TaskLog.Run(ws, "prepare", "명령: prepare --no-csv", () =>
            {
                Log.Info("첫 줄\n둘째 줄");
                Log.Warn("주의");
                return 5;
            });
            Assert.Equal(5, code);
            var ex = Assert.Throws<ToolException>(() => TaskLog.Run(ws, "build", null, () => throw new ToolException("만들지 못함")));
            Assert.Equal("만들지 못함", ex.Message);
        }
        Assert.Contains("첫 줄\n둘째 줄", console.Lines); // 원래 보고 대상도 그대로 받는다

        var files = TaskLog.Recent(ws, 10);
        Assert.Equal(2, files.Count);
        string prepare = File.ReadAllText(files.Single(f => f.EndsWith("-prepare.log")));
        Assert.Contains("명령: prepare --no-csv", prepare);
        Assert.Contains(ws.Root, prepare);
        Assert.Contains("첫 줄", prepare);
        Assert.Contains("둘째 줄", prepare);
        Assert.Contains("WARN  주의", prepare);
        Assert.Contains("종료 코드 5", prepare);
        string build = File.ReadAllText(files.Single(f => f.EndsWith("-build.log")));
        Assert.Contains("error: 만들지 못함", build);
        Assert.Contains("종료 코드 1", build);
    }

    [Fact]
    public void UnwritableLogFolderDoesNotStopTheWork()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Dir("ws"));
        tmp.File("ws/logs", "logs 가 폴더가 아니라 파일"); // 기록 폴더를 만들 수 없음
        Assert.Null(TaskLog.Open(ws, "prepare"));
        bool ran = false;
        Assert.Equal(0, TaskLog.Run(ws, "prepare", null, () => { ran = true; return 0; }));
        Assert.True(ran);
    }
}

// 오류 기록: 덮어쓰지 않고 이어 쓰며, 일정 크기를 넘으면 뒷부분만 남긴다
[Collection("error-log")]
public class ErrorLogTests
{
    [Fact]
    public void AppendsAndKeepsTail()
    {
        using var tmp = new TempDir();
        string? old = Errors.LogPathOverride;
        Errors.LogPathOverride = Path.Combine(tmp.Path, "error.log");
        try
        {
            Errors.WriteLog("첫 번째 내용", "ctx-1");
            Errors.WriteLog(new InvalidOperationException("두 번째"), "ctx-2");
            string text = File.ReadAllText(Errors.LogPath);
            Assert.StartsWith("===== ", text);
            Assert.True(text.IndexOf("ctx-1", StringComparison.Ordinal) < text.IndexOf("ctx-2", StringComparison.Ordinal));
            Assert.Contains("첫 번째 내용", text);
            Assert.Contains("두 번째", text);

            string big = new('x', 10_000);
            for (int i = 0; i < 60; i++) Errors.WriteLog(big, $"fill-{i}");
            Errors.WriteLog("마지막", "last");
            var length = new FileInfo(Errors.LogPath).Length;
            Assert.True(length <= Errors.MaxLogBytes, $"{length} bytes");
            text = File.ReadAllText(Errors.LogPath);
            Assert.StartsWith("===== ", text); // 한 건의 머리 줄부터
            Assert.DoesNotContain("ctx-1", text); // 오래된 것은 잘림
            Assert.EndsWith("last\n마지막\n\n", text);
        }
        finally
        {
            Errors.LogPathOverride = old;
        }
    }
}
