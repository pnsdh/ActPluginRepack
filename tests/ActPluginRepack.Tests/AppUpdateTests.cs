using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ActPluginRepack.Tests;

// 자동 업데이트의 바꿔 넣기 / 되돌리기 / 정리 (받기와 확인은 아래 AppUpdateDownloadTests)
// AppUpdate.Step(정적)을 바꾸는 시험이 있어 이 클래스 안에서만 차례로 돈다 (Step 을 쓰는 곳은 이 클래스뿐 — 받기 시험은
// DownloadsDir 만 바꾸고 바꿔 넣기는 하지 않는다)
public class AppUpdateTests
{
    private static (TempDir Tmp, string App, string Staging) Setup()
    {
        var tmp = new TempDir();
        string app = tmp.Dir("app");
        tmp.File("app/ActPluginRepack.Gui.exe", "gui 1");
        tmp.File("app/cli/ActPluginRepack.exe", "cli 1");
        tmp.File("app/사용법.txt", "guide 1");
        tmp.File("app/my-notes.txt", "user file");          // 묶음에 없는 사용자 파일
        string staging = tmp.Dir("staging");
        tmp.File("staging/ActPluginRepack.Gui.exe", "gui 2");
        tmp.File("staging/cli/ActPluginRepack.exe", "cli 2");
        tmp.File("staging/사용법.txt", "guide 2");
        tmp.File("staging/licenses/New-OFL.txt", "new");     // 새로 생긴 파일
        return (tmp, app, staging);
    }

    [Fact]
    public void ApplyReplacesAndKeepsOldUntilCleanup()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            AppUpdate.Apply(staging, app);
            Assert.Equal("gui 2", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe")));
            Assert.Equal("cli 2", File.ReadAllText(Path.Combine(app, "cli", "ActPluginRepack.exe")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(app, "licenses", "New-OFL.txt")));
            Assert.Equal("gui 1", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe.old")));
            Assert.Equal("user file", File.ReadAllText(Path.Combine(app, "my-notes.txt")));

            AppUpdate.CleanupOld(app);
            Assert.Empty(Directory.GetFiles(app, "*.old", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(app, "my-notes.txt")));
            AssertNoLeftovers(app);
        }
    }

    // 업데이트가 끝난 뒤 남는 것이 없어야: .old / .new / 일지
    private static void AssertNoLeftovers(string app)
    {
        Assert.Empty(Directory.GetFiles(app, "*.new", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(app, AppUpdate.JournalFile)));
    }

    private static void AssertOriginal(string app)
    {
        Assert.Equal("gui 1", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe")));
        Assert.Equal("cli 1", File.ReadAllText(Path.Combine(app, "cli", "ActPluginRepack.exe")));
        Assert.Equal("guide 1", File.ReadAllText(Path.Combine(app, "사용법.txt")));
        Assert.False(File.Exists(Path.Combine(app, "licenses", "New-OFL.txt")));
        Assert.Equal("user file", File.ReadAllText(Path.Combine(app, "my-notes.txt")));
        Assert.Empty(Directory.GetFiles(app, "*.old", SearchOption.AllDirectories));
        AssertNoLeftovers(app);
    }

    // 도중에 프로세스가 죽은 것처럼: step 에서 (IO 가 아닌) 예외를 던져 되돌리기 없이 빠져나간다
    private static void ApplyAndDieAt(string staging, string app, string step)
    {
        AppUpdate.Step = s => { if (s == step) throw new InvalidOperationException("죽음"); };
        try { Assert.Throws<InvalidOperationException>(() => AppUpdate.Apply(staging, app)); }
        finally { AppUpdate.Step = null; }
        Assert.True(File.Exists(Path.Combine(app, AppUpdate.JournalFile)));
    }

    [Fact]
    public void InterruptedSwapIsRevertedOnNextStart()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            ApplyAndDieAt(staging, app, "swap:" + Path.Combine("cli", "ActPluginRepack.exe")); // cli 는 바꿨고 GUI 는 아직
            Assert.Equal("cli 2", File.ReadAllText(Path.Combine(app, "cli", "ActPluginRepack.exe")));
            Assert.Equal("gui 1", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe"))); // GUI exe 는 맨 끝이라 원래 것

            AppUpdate.CleanupOld(app); // 다음 시작
            AssertOriginal(app);
        }
    }

    // 이름 바꾸기를 다 하고 "끝남"만 못 적었으면 끝난 업데이트 (다음 시작은 이미 새 GUI exe — 되돌리면 섞인다)
    [Fact]
    public void SwapFinishedButNotRecordedIsKept()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            ApplyAndDieAt(staging, app, "swap:ActPluginRepack.Gui.exe"); // 맨 끝인 GUI exe 까지 바꾼 직후
            AppUpdate.CleanupOld(app);
            Assert.Equal("gui 2", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe")));
            Assert.Equal("cli 2", File.ReadAllText(Path.Combine(app, "cli", "ActPluginRepack.exe")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(app, "licenses", "New-OFL.txt")));
            Assert.Empty(Directory.GetFiles(app, "*.old", SearchOption.AllDirectories));
            AssertNoLeftovers(app);
        }
    }

    [Fact]
    public void InterruptedCopyLeavesOriginalsAndIsCleanedOnNextStart()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            ApplyAndDieAt(staging, app, "copy:사용법.txt");
            Assert.Equal("gui 1", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe")));
            AppUpdate.CleanupOld(app);
            AssertOriginal(app);
        }
    }

    [Fact]
    public void UserOldFilesAreNeverTouched()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            // 사용자가 바탕화면·다운로드처럼 자기 파일이 있는 곳에 풀어 둔 경우: 짝이 있는 .old 라도 우리 것이 아니다
            tmp.File("app/보고서.xlsx", "report");
            tmp.File("app/보고서.xlsx.old", "report old");
            tmp.File("app/cli/settings.ini", "x");
            tmp.File("app/cli/settings.ini.old", "x old");

            AppUpdate.Apply(staging, app);
            AppUpdate.Revert(app);
            AppUpdate.Apply(staging, app);
            AppUpdate.CleanupOld(app);
            AppUpdate.CleanupOld(app);

            Assert.Equal("report old", File.ReadAllText(Path.Combine(app, "보고서.xlsx.old")));
            Assert.Equal("x old", File.ReadAllText(Path.Combine(app, "cli", "settings.ini.old")));
            Assert.Equal("gui 2", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe")));
            Assert.False(File.Exists(Path.Combine(app, "ActPluginRepack.Gui.exe.old")));
            AssertNoLeftovers(app);
        }
    }

    [Fact]
    public void OldFileInUseIsDeletedLater()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            AppUpdate.Apply(staging, app);
            string oldGui = Path.Combine(app, "ActPluginRepack.Gui.exe.old");
            using (File.Open(oldGui, FileMode.Open, FileAccess.Read, FileShare.Read)) // 이전 버전이 아직 끝나는 중
            {
                AppUpdate.CleanupOld(app);
                Assert.True(File.Exists(oldGui));
                Assert.False(File.Exists(Path.Combine(app, "cli", "ActPluginRepack.exe.old")));
            }
            AppUpdate.CleanupOld(app); // 다음 시작
            Assert.False(File.Exists(oldGui));
            AssertNoLeftovers(app);
        }
    }

    [Fact]
    public void RevertRestoresPreviousFiles()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            AppUpdate.Apply(staging, app);
            AppUpdate.Revert(app); // 새 버전을 실행하지 못했을 때
            AssertOriginal(app);
        }
    }

    // 되돌리기가 한 파일에서 막혀 일지가 "바꾸는 중"으로 남으면, 다음 시작은 끝난 업데이트로 보지 않고 마저 되돌린다
    [Fact]
    public void PartlyRevertedUpdateIsFinishedRevertingNotRolledForward()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            AppUpdate.Apply(staging, app);
            string cli = Path.Combine(app, "cli", "ActPluginRepack.exe");
            using (new FileStream(cli, FileMode.Open, FileAccess.Read, FileShare.Read)) // 새 cli 를 지우지 못함
                AppUpdate.Revert(app);
            Assert.Equal("gui 1", File.ReadAllText(Path.Combine(app, "ActPluginRepack.Gui.exe"))); // GUI 는 이미 되돌림

            AppUpdate.CleanupOld(app); // 다음 시작
            AssertOriginal(app);
        }
    }

    [Fact]
    public void FailedApplyRollsBack()
    {
        var (tmp, app, staging) = Setup();
        using (tmp)
        {
            // 바꿔 넣는 도중 실패: 사용법.txt 를 다른 프로그램이 열고 있어 비켜 두지 못함
            using (File.Open(Path.Combine(app, "사용법.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<ToolException>(() => AppUpdate.Apply(staging, app));
            AssertOriginal(app);
        }
    }

    [Fact]
    public void CleanupIgnoresFoldersThatAreNotThePackage()
    {
        using var tmp = new TempDir();
        string desktop = tmp.Dir("desktop");             // 사용자가 exe 만 꺼내 둔 곳 (cli\ 없음)
        tmp.File("desktop/report.docx.old", "keep");
        tmp.File("desktop/report.docx", "x");
        AppUpdate.CleanupOld(desktop);
        Assert.True(File.Exists(Path.Combine(desktop, "report.docx.old")));
        Assert.False(AppUpdate.Supported(desktop));
    }
}

// 받은 묶음 확인: SHA256, 서명, 구성·버전이 하나라도 틀리면 아무것도 바꾸기 전에 멈춘다.
// ACTREPACK_UPDATE_FEED(로컬 폴더)로 받는다 — 네트워크 없음. 공개키를 시험용으로 바꾸므로 "signature" 묶음에서.
// 받은 것은 임시 폴더에 푼다 (사용자의 %LOCALAPPDATA%\ActPluginRepack\update 는 건드리지 않음).
[Collection("signature")]
public class AppUpdateDownloadTests
{
    // 버전 정보가 있는 실행 파일 대신 이 시험 어셈블리 (FileVersion = Directory.Build.props 의 버전)
    private static readonly string Versioned = typeof(AppUpdateDownloadTests).Assembly.Location;
    private static Version Built => Version.Parse(FileVersionInfo.GetVersionInfo(Versioned).FileVersion!);

    // 배포 zip 모양 (ActPluginRepack\ 아래 GUI exe 와 cli\ActPluginRepack.exe). 디스크에 exe 를 쓰지 않고 메모리에서 한 번만
    private static readonly Lazy<byte[]> Zip = new(() =>
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("ActPluginRepack/" + AppUpdate.GuiExe).Open()) s.Write(File.ReadAllBytes(Versioned));
            using (var s = zip.CreateEntry("ActPluginRepack/cli/ActPluginRepack.exe").Open()) s.Write("cli"u8);
        }
        return ms.ToArray();
    });

    // feed 폴더에 publish.ps1 이 올리는 것과 같은 zip·.sha256·.sig 를 만들고, 그 feed 로 Resolve -> Download
    private static void WithFeed(Version version, Action<string, string, Func<string>> test)
    {
        string originalKey = UpdateSignature.PublicKey;
        string? originalFeed = Environment.GetEnvironmentVariable("ACTREPACK_UPDATE_FEED");
        string originalDownloads = AppUpdate.DownloadsDir;
        using var tmp = new TempDir();
        try
        {
            var (pem, publicKey) = UpdateSignature.Generate();
            UpdateSignature.PublicKey = publicKey;
            string feed = tmp.Dir("feed");
            AppUpdate.DownloadsDir = tmp.Dir("downloads");
            string zip = Path.Combine(feed, AppUpdate.AssetName(version));
            File.WriteAllBytes(zip, Zip.Value);
            string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)));
            File.WriteAllText(zip + ".sha256", $"{sha}  {Path.GetFileName(zip)}\n");
            File.WriteAllText(zip + ".sig", UpdateSignature.Sign(pem, Path.GetFileName(zip), sha) + "\n");

            Environment.SetEnvironmentVariable("ACTREPACK_UPDATE_FEED", feed);
            test(zip, pem, () => AppUpdate.Download(AppUpdate.Resolve(new AppInfo.Update($"v{version.ToString(3)}", version, ""))));
        }
        finally
        {
            UpdateSignature.PublicKey = originalKey;
            Environment.SetEnvironmentVariable("ACTREPACK_UPDATE_FEED", originalFeed);
            AppUpdate.DownloadsDir = originalDownloads;
        }
    }

    private static string Fails(Func<string> download) => Assert.Throws<ToolException>(() => download()).Message;

    [Fact]
    public void AcceptsSignedPackage() =>
        WithFeed(Built, (_, _, download) =>
        {
            string root = download();
            Assert.True(File.Exists(Path.Combine(root, AppUpdate.GuiExe)));
            Assert.True(AppUpdate.Supported(root));
        });

    [Fact]
    public void RejectsHashMismatch() =>
        WithFeed(Built, (zip, _, download) =>
        {
            File.WriteAllText(zip + ".sha256", new string('0', 64));         // zip 이 바뀜 (또는 받다가 깨짐)
            Assert.Contains("SHA256 불일치", Fails(download));
            File.WriteAllText(zip + ".sha256", "not a hash");
            Assert.Contains("형식", Fails(download));
            File.Delete(zip + ".sha256");
            Assert.Contains("SHA256 파일", Fails(download));
        });

    [Fact]
    public void RejectsMissingOrWrongSignature() =>
        WithFeed(Built, (zip, pem, download) =>
        {
            string sha = File.ReadAllText(zip + ".sha256").Split(' ')[0];
            var (otherPem, _) = UpdateSignature.Generate();
            File.WriteAllText(zip + ".sig", UpdateSignature.Sign(otherPem, Path.GetFileName(zip), sha)); // 다른 사람의 키
            Assert.Contains("서명이 맞지 않아", Fails(download));
            File.WriteAllText(zip + ".sig", UpdateSignature.Sign(pem, AppUpdate.AssetName(new Version(1, 0, 0)), sha)); // 다른 버전 묶음의 서명
            Assert.Contains("서명이 맞지 않아", Fails(download));
            File.WriteAllText(zip + ".sig", "not base64!");
            Assert.Contains("서명이 맞지 않아", Fails(download));
            File.Delete(zip + ".sig");
            Assert.Contains("서명 파일", Fails(download));
        });

    // 이름·서명은 맞는데 안의 프로그램 버전이 다름 (다른 버전 묶음을 그 이름으로 올림)
    [Fact]
    public void RejectsVersionMismatch() =>
        WithFeed(new Version(Built.Major, Built.Minor, Built.Build + 1000), (_, _, download) =>
            Assert.Contains("버전이 예상과 다릅니다", Fails(download)));
}
