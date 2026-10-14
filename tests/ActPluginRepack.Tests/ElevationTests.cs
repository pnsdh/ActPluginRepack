using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ActPluginRepack.Tests;

// 관리자 권한 경로. UAC 확인 창 자체는 자동으로 누를 수 없으므로(보안 데스크톱), 그 앞뒤를 나눠 시험한다:
//   - 이미 관리자인 프로세스에서 runas 로 띄우면 확인 창 없이 올라간다 -> 실제 CLI 를 관리자로 띄워 설치/복원/실패/
//     ACT 실행 중 종료 코드와 기록 재생까지 끝에서 끝까지 (Program Files 처럼 Users 는 읽기만 되는 폴더에, 공백·한글 경로로)
//   - 확인 창의 '아니요'는 프로세스 시작을 바꿔 ERROR_CANCELLED 를 흉내
// 일반 권한에서 "관리자 필요"를 알아채는 쪽은 scratchpad 의 수동 스크립트(중간 무결성으로 CLI 실행)로 확인했다.
// ElevatedRunner.Start 를 바꾸고 게임이 켜진 것처럼 프로세스를 띄우는 시험이 있어, 그것을 쓰거나 보는 시험은 이 묶음에서
// 차례로 돈다: 설치 시험(InstallTests / InstallRollbackTests / InstallRestoreTests — 게임이 켜져 있으면 설치를 거부). 둘 다 이 묶음 밖에서는
// 쓰지 않으므로 다른 묶음과는 동시에 돌아도 된다.
[CollectionDefinition("elevation")]
public sealed class ElevationCollection;

[Collection("elevation")]
public class ElevationTests
{
    private sealed class Capture : IReporter
    {
        public readonly List<(LogKind Kind, string Message)> Lines = [];
        public void Log(LogKind kind, string message) { lock (Lines) Lines.Add((kind, message)); }
    }

    // 이 시험 어셈블리와 같은 구성(Debug/Release)·대상 프레임워크로 빌드된 CLI — 시험 프로젝트가 CLI 를 같은 구성으로 같이
    // 빌드한다 (...\tests\ActPluginRepack.Tests\bin\<구성>\<tfm>\ -> ...\src\ActPluginRepack.Cli\bin\<구성>\<tfm>\ActPluginRepack.exe)
    private static string? Cli()
    {
        var output = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string tfm = output.Name, configuration = output.Parent?.Name ?? "Release";
        for (var d = output; d != null; d = d.Parent)
        {
            string exe = Path.Combine(d.FullName, "src", "ActPluginRepack.Cli", "bin", configuration, tfm, "ActPluginRepack.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private static bool CanRun => OperatingSystem.IsWindows() && Elevation.IsElevated && Cli() != null;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    // Program Files 와 같은 권한: 상속 끊고 Administrators/SYSTEM 전체, Users 는 읽기·실행만
    private static void ProtectLikeProgramFiles(string dir)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(acl);
    }

    [Fact]
    public void ElevatedInstallAndRestoreEndToEnd()
    {
        if (!CanRun) Assert.Skip("관리자 셸에서만 시험할 수 있습니다");
        using var tmp = new TempDir();
        string ws = Path.Combine(tmp.Path, "작업 폴더 (x86)");
        string act = Path.Combine(tmp.Path, "Program Files (x86)", "Advanced Combat Tracker");
        string pluginDir = Path.Combine(act, "Plugins", "FFXIV_ACT_Plugin_KR");
        string overlayDir = Path.Combine(act, "Plugins", "OverlayPlugin", "resources");
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(overlayDir);
        File.WriteAllText(Path.Combine(pluginDir, "FFXIV_ACT_Plugin.dll"), "official");
        File.WriteAllText(Path.Combine(pluginDir, "deucalion-1.5.0.dll"), "old deucalion");
        File.WriteAllText(Path.Combine(overlayDir, "opcodes.jsonc"), "official opcodes");
        File.WriteAllText(Path.Combine(act, "Plugins", "OverlayPlugin", "OverlayPlugin.dll"), "overlay");
        Directory.CreateDirectory(Path.Combine(ws, "output"));
        File.WriteAllText(Path.Combine(ws, "output", "FFXIV_ACT_Plugin.dll"), "patched");
        File.WriteAllText(Path.Combine(ws, "output", "deucalion-1.5.1.distrib.dll"), "new deucalion");
        Directory.CreateDirectory(Path.Combine(ws, "extras", "OverlayPlugin"));
        File.WriteAllText(Path.Combine(ws, "extras", "OverlayPlugin", "opcodes.jsonc"), "patched opcodes");
        if (OperatingSystem.IsWindows()) ProtectLikeProgramFiles(act);

        var capture = new Capture();
        using (Operation.Begin(capture, TestContext.Current.CancellationToken))
        {
            int code = ElevatedRunner.Run(Cli()!, "install", ["--root", ws, "--target", pluginDir, "--overlay", overlayDir]);
            Assert.Equal(0, code);
        }
        Assert.Equal("patched", File.ReadAllText(Path.Combine(pluginDir, "FFXIV_ACT_Plugin.dll")));
        Assert.Equal("patched opcodes", File.ReadAllText(Path.Combine(overlayDir, "opcodes.jsonc")));
        Assert.Contains(capture.Lines, l => l.Kind == LogKind.Done && l.Message.Contains("[install]")); // 관리자 쪽 기록이 재생됨
        string backup = Assert.Single(Directory.GetDirectories(Path.Combine(ws, "backups")));

        capture.Lines.Clear();
        using (Operation.Begin(capture, TestContext.Current.CancellationToken))
            Assert.Equal(0, ElevatedRunner.Run(Cli()!, "restore", ["--root", ws, "--backup", backup]));
        Assert.Equal("official", File.ReadAllText(Path.Combine(pluginDir, "FFXIV_ACT_Plugin.dll")));
        Assert.Equal("official opcodes", File.ReadAllText(Path.Combine(overlayDir, "opcodes.jsonc")));
        Assert.True(File.Exists(Path.Combine(pluginDir, "deucalion-1.5.0.dll")));
        Assert.False(Directory.GetFiles(pluginDir, "deucalion-1.5.1*").Any());

        // 관리자 쪽 실패: 기록(오류 줄)이 돌아오고 종료 코드 1
        capture.Lines.Clear();
        using (Operation.Begin(capture, TestContext.Current.CancellationToken))
            Assert.Equal(1, ElevatedRunner.Run(Cli()!, "restore", ["--root", ws, "--backup", Path.Combine(ws, "backups", "없는 폴더")]));
        Assert.Contains(capture.Lines, l => l.Kind == LogKind.Error);
    }

    [Fact]
    public void ElevatedSideReportsActRunningAsExitCode4()
    {
        if (!CanRun) Assert.Skip("관리자 셸에서만 시험할 수 있습니다");
        using var tmp = new TempDir();
        // 게임이 켜진 것처럼: ping 을 ffxiv_dx11.exe 이름으로 잠깐 띄운다
        string fake = Path.Combine(tmp.Path, "ffxiv_dx11.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
        using var game = Process.Start(new ProcessStartInfo(fake, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            string ws = Path.Combine(tmp.Path, "ws");
            string pluginDir = Path.Combine(tmp.Path, "plugin");
            Directory.CreateDirectory(Path.Combine(ws, "output"));
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(ws, "output", "FFXIV_ACT_Plugin.dll"), "patched");
            File.WriteAllText(Path.Combine(pluginDir, "FFXIV_ACT_Plugin.dll"), "official");
            using (Operation.Begin(new Capture(), TestContext.Current.CancellationToken))
                Assert.Equal(4, ElevatedRunner.Run(Cli()!, "install", ["--root", ws, "--target", pluginDir, "--overlay", "-"]));
            Assert.Equal("official", File.ReadAllText(Path.Combine(pluginDir, "FFXIV_ACT_Plugin.dll"))); // 아무것도 안 바뀜
        }
        finally
        {
            game.Kill();
            game.WaitForExit(5000);
        }
    }

    [Fact]
    public void ConfirmDialogNoBecomesElevationCanceled()
    {
        var original = ElevatedRunner.Start;
        ElevatedRunner.Start = _ => throw new Win32Exception(1223); // ERROR_CANCELLED: 확인 창에서 '아니요'
        try
        {
            Assert.Throws<ElevationCanceledException>(() => ElevatedRunner.Run("ActPluginRepack.exe", "install", []));
        }
        finally { ElevatedRunner.Start = original; }
    }

    [Fact]
    public void BrokenLogLineDoesNotHideResult()
    {
        // 관리자 쪽이 도중에 끝나 마지막 줄이 잘린 기록: 앞 줄은 재생하고, 진짜 결과(여기선 시작 실패)를 가리지 않고, 파일은 지운다
        string? log = null, cwd = null;
        var original = ElevatedRunner.Start;
        ElevatedRunner.Start = psi =>
        {
            cwd = psi.WorkingDirectory;
            string marker = ElevatedRunner.LogOption + " ";
            log = psi.Arguments[(psi.Arguments.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim('"');
            File.WriteAllText(log, "{\"Kind\":1,\"Message\":\"앞 줄\"}\n{\"Kind\":5,\"Mess", new System.Text.UTF8Encoding(false));
            return null;
        };
        var capture = new Capture();
        try
        {
            using (Operation.Begin(capture, TestContext.Current.CancellationToken))
                Assert.Throws<ToolException>(() => ElevatedRunner.Run(@"C:\앱 폴더\cli\ActPluginRepack.exe", "install", []));
            Assert.Contains(capture.Lines, l => l == (LogKind.Info, "앞 줄"));
            Assert.False(File.Exists(log));
            Assert.Equal(@"C:\앱 폴더\cli", cwd); // runas 의 기본(System32)이 아니라 CLI 폴더에서
        }
        finally { ElevatedRunner.Start = original; }
    }

    [Fact]
    public void NoElevateEnvironmentStopsBeforeDialog()
    {
        bool started = false;
        var original = ElevatedRunner.Start;
        ElevatedRunner.Start = _ => { started = true; return null; };
        Environment.SetEnvironmentVariable("ACTREPACK_NO_ELEVATE", "1");
        try
        {
            Assert.Throws<ToolException>(() => ElevatedRunner.Run("ActPluginRepack.exe", "install", []));
            Assert.False(started);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTREPACK_NO_ELEVATE", null);
            ElevatedRunner.Start = original;
        }
    }

    [Fact]
    public void WorkspaceLockBlocksSecondJob()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        using (ws.Lock())
            Assert.Throws<WorkspaceBusyException>(() => ws.Lock());
        ws.Lock().Dispose(); // 놓은 뒤에는 다시 잡힌다
        // 없는 폴더는 '다른 작업 실행 중'이 아니라 그대로 알린다 (Throws 는 정확한 형식만 — WorkspaceBusyException 이면 실패)
        Assert.Throws<ToolException>(() => new Workspace(Path.Combine(tmp.Path, "없음")).Lock());
    }
}
