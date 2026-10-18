using System.Windows;
using System.Windows.Threading;

namespace ActPluginRepack.Gui;

public partial class App : Application
{
    // 한 번에 하나만: 두 번 눌러 또 열면 이미 열린 창을 앞으로 가져오고 끝낸다 (두 창이 같은 작업 폴더를 같이 건드리지 않게)
    private static System.Threading.Mutex? _single;

    // 자동 업데이트 직후: 이전 버전 (창 위쪽 "업데이트 완료" 알림에 "... 버전으로 업데이트했습니다. (이전 ...)")
    public static string? UpdatedFrom { get; private set; }

    // 자동 업데이트로 막 바뀐 새 버전이 아직 창을 그리지 못함 — 이 사이에 실패하면 이전 버전으로 되돌린다
    // (이전 버전은 시작 신호를 받고 이미 끝났으므로 여기서 되돌리지 않으면 고장 난 새 버전만 남는다)
    private static bool _updateUnconfirmed;

    protected override void OnStartup(StartupEventArgs e)
    {
        Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1"); // 이름만 준 실행 파일(gh, git, py 등)을 현재 폴더에서 찾지 않게
        // 자동 업데이트가 띄운 새 버전: 이전 버전이 끝날 때까지 기다린 뒤 시작한다 (하나만 열리게, .old 를 지울 수 있게)
        bool afterUpdate = false;
        for (int i = 0; i + 1 < e.Args.Length; i++)
        {
            if (e.Args[i] == "--wait-pid" && int.TryParse(e.Args[i + 1], out int pid))
            {
                afterUpdate = true;
                ViewModels.UpdateViewModel.SignalStarted(pid); // 이전 버전에 "실행됨"을 알린다 (그래야 이전 버전이 끝난다)
                try
                {
                    using var old = System.Diagnostics.Process.GetProcessById(pid);
                    old.WaitForExit(15_000);
                }
                catch (ArgumentException) { } // 이미 끝남
            }
            if (e.Args[i] == "--updated-from") UpdatedFrom = e.Args[i + 1];
        }

        // 업데이트 직후에는 이전 버전이 아직 끝나는 중일 수 있으므로 잠시 기다린다 (다른 창으로 보고 조용히 끝나지 않게)
        bool first;
        try
        {
            _single = new System.Threading.Mutex(false, @"Local\ActPluginRepack.Gui");
            first = _single.WaitOne(afterUpdate ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        }
        catch (System.Threading.AbandonedMutexException) { first = true; } // 이전 실행이 놓지 않고 끝남 -> 이제 이쪽 것
        // 관리자 권한으로 띄운 창이 만든 잠금은 일반 권한으로 열 수 없다 -> 이미 실행 중인 것으로 본다
        catch (UnauthorizedAccessException) { first = false; }
        if (!first)
        {
            BringExistingToFront();
            Shutdown();
            return;
        }
        // 받아 둔 업데이트 묶음 정리. 이전 파일(.old)은 창이 뜬 뒤에 지운다 — 새 버전의 창이 뜨지 않으면 그것으로 되돌린다.
        // 도중에 멈춘(일부만 되돌린) 업데이트는 창을 만들기 전에 마저 되돌린다 (섞인 파일로 창을 만들다 실패하지 않게)
        AppUpdate.CleanupDownloads();
        if (UpdatedFrom == null) AppUpdate.FinishInterrupted(AppContext.BaseDirectory);
        // 화면 코드의 예상 못 한 오류도 앱을 죽이지 않고 알린다 (전체 내용은 오류 로그 파일로)
        DispatcherUnhandledException += OnUnhandled;
        _updateUnconfirmed = UpdatedFrom != null;
        base.OnStartup(e);
        // 창은 여기서 연다 (StartupUri 를 쓰면 두 번째 실행에서 Shutdown 한 뒤에도 창이 만들어질 수 있다).
        // 창을 만들다 실패하면 알리고 끝낸다 — 창 없이 프로세스만 남으면 잠금(mutex) 때문에 다시 실행해도 열리지 않는다.
        try
        {
            // 항상 다크 모드 (시스템 설정과 상관없이)
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            // 강조색은 시스템 강조색(회색일 수도 있음) 대신 눈에 띄는 파랑으로 — 주 버튼이 어디인지 바로 보이게
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
                System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6), Wpf.Ui.Appearance.ApplicationTheme.Dark);
            var window = new MainWindow();
            // 새 창이 실제로 그려지면 업데이트를 마친 것으로 보고, 지난 자동 업데이트가 비켜 둔 이전 파일(.old)을 정리한다
            window.ContentRendered += (_, _) =>
            {
                _updateUnconfirmed = false;
                AppUpdate.CleanupOld(AppContext.BaseDirectory);
            };
            window.Show();
        }
        catch (Exception ex)
        {
            if (_updateUnconfirmed && TryRevertUpdate(ex)) return;
            Errors.WriteLog(ex, "gui");
            // 창이 없어 도움말의 [문제 보고용 기록 저장]을 쓸 수 없다 — 오류 기록 파일을 직접 올리게
            MessageBox.Show($"프로그램을 시작하지 못했습니다.\n\n{Errors.Describe(ex)}\n\n자세한 내용: {Errors.LogPath}\n\n" +
                            $"계속되면 이 오류 기록 파일을 디스코드({AppInfo.DiscordUrl})에 올리고 어떤 문제인지 적어 주세요.",
                            Titles.App, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // 자동 업데이트 직후 새 버전의 창을 만들지 못함: 원래 버전으로 되돌리고 그 버전을 다시 띄운다 (비켜 둔 .old 가 아직 있다)
    private bool TryRevertUpdate(Exception ex)
    {
        Errors.WriteLog(ex, "gui after update");
        _updateUnconfirmed = false;
        string appDir = AppContext.BaseDirectory;
        AppUpdate.RevertResult result;
        try { result = AppUpdate.Revert(appDir); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Errors.WriteLog(e, "gui revert update");
            result = AppUpdate.RevertResult.Partial;
        }
        if (result == AppUpdate.RevertResult.NothingToRevert) return false; // 되돌릴 업데이트가 없음 — 일반 안내로
        if (result == AppUpdate.RevertResult.Partial)
        {
            // 일부만 되돌렸으면 새 GUI 가 옛 파일 위에서 계속 돌지 않게 끝낸다 (다음 시작 때 마저 되돌린다)
            MessageBox.Show($"새 버전을 시작하지 못했고, 이전 버전으로 다 되돌리지 못했습니다. 이 프로그램을 다시 실행하면 마저 되돌립니다. " +
                            $"그래도 안 켜지면 {AppInfo.ReleasesUrl} 에서 새로 받아 새 폴더에 푸세요.\n\n{Errors.Describe(ex)}\n\n" +
                            $"자세한 내용: {Errors.LogPath}\n\n계속되면 이 오류 기록 파일을 디스코드({AppInfo.DiscordUrl})에 올리고 어떤 문제인지 적어 주세요.",
                            Titles.App, MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return true;
        }
        // (곧 이전 버전이 다시 뜬다 — 그 창의 도움말에서 문제 보고를 할 수 있다)
        MessageBox.Show($"새 버전을 시작하지 못해 이전 버전({UpdatedFrom})으로 되돌렸습니다. 확인을 누르면 이전 버전이 다시 켜지고, 그대로 쓰면 됩니다.\n\n" +
                        $"{Errors.Describe(ex)}\n\n자세한 내용: {Errors.LogPath}\n\n" +
                        $"새 버전이 계속 켜지지 않으면 창 오른쪽 위 [{Titles.Help}]에서 {Notes.ReportToDiscord}",
                        Titles.App, MessageBoxButton.OK, MessageBoxImage.Warning);
        // 되돌린 이전 버전을 띄운다 (이 프로세스가 끝나기를 기다렸다가 시작한다)
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(appDir, AppUpdate.GuiExe))
            {
                UseShellExecute = false,
                ArgumentList = { "--wait-pid", Environment.ProcessId.ToString() },
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception || Errors.IsFileError(e))
        {
            Errors.WriteLog(e, "gui restart previous");
        }
        Shutdown(1);
        return true;
    }

    private static void BringExistingToFront()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private static bool _showing;
    private static string? _lastShown;

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // 업데이트 직후 새 창이 그려지기 전의 오류: 이전 버전으로 되돌리고 끝낸다
        if (_updateUnconfirmed && Current is App app && app.TryRevertUpdate(e.Exception)) return;
        Errors.WriteLog(e.Exception, "gui");
        // 같은 오류가 되풀이되거나 알림 창이 이미 떠 있으면 기록만 한다 (알림 창이 쏟아지지 않게)
        string message = Errors.Describe(e.Exception);
        // 창이 없거나 보이지 않는 채로 남으면(시작 도중의 오류 등) 알린 뒤 끝낸다 — 보이지 않는 프로세스만 남지 않게
        bool noWindow = Current?.MainWindow is not { IsVisible: true };
        if (!_showing && message != _lastShown)
        {
            _showing = true;
            _lastShown = message;
            try
            {
                // 제목과 같은 말이 되풀이되지 않게, 분류되지 않은 오류는 종류와 원문만 덧붙인다 (고칠 때 필요)
                string detail = Errors.Recoverable(e.Exception) ? message : $"{e.Exception.GetType().Name}: {e.Exception.Message}";
                // 창이 남아 있으면 도움말 화면의 기록 저장으로, 끝내야 하면(창이 없음) 오류 기록 파일을 직접
                string report = noWindow
                    ? $"계속되면 이 오류 기록 파일을 디스코드({AppInfo.DiscordUrl})에 올리고 어떤 문제인지 적어 주세요."
                    : $"계속되면 창 오른쪽 위 [{Titles.Help}]에서 {Notes.ReportToDiscord}";
                MessageBox.Show($"예상하지 못한 문제가 생겼습니다.\n\n{detail}\n\n자세한 내용: {Errors.LogPath}\n\n{report}",
                                Titles.App, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _showing = false; }
        }
        if (noWindow) Current?.Shutdown(1);
    }
}
