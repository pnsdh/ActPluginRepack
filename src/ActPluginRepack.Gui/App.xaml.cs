using System.Windows;
using System.Windows.Threading;

namespace ActPluginRepack.Gui;

public partial class App : Application
{
    // 한 번에 하나만: 두 번 눌러 또 열면 이미 열린 창을 앞으로 가져오고 끝낸다 (두 창이 같은 작업 폴더를 같이 건드리지 않게)
    private static System.Threading.Mutex? _single;

    // 자동 업데이트 직후: 이전 버전 (창 위쪽 "업데이트 완료" 알림에 "... 버전으로 업데이트했습니다. (이전 ...)")
    public static string? UpdatedFrom { get; private set; }

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
        _single = new System.Threading.Mutex(false, @"Local\ActPluginRepack.Gui");
        bool first;
        try { first = _single.WaitOne(afterUpdate ? TimeSpan.FromSeconds(30) : TimeSpan.Zero); }
        catch (System.Threading.AbandonedMutexException) { first = true; } // 이전 실행이 놓지 않고 끝남 -> 이제 이쪽 것
        if (!first)
        {
            BringExistingToFront();
            Shutdown();
            return;
        }
        // 지난 자동 업데이트가 비켜 둔 이전 파일(.old) 정리
        AppUpdate.CleanupOld(AppContext.BaseDirectory);
        AppUpdate.CleanupDownloads();
        // 화면 코드의 예상 못 한 오류도 앱을 죽이지 않고 알린다 (전체 내용은 오류 로그 파일로)
        DispatcherUnhandledException += OnUnhandled;
        // 항상 다크 모드 (시스템 설정과 상관없이)
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        // 강조색은 시스템 강조색(회색일 수도 있음) 대신 눈에 띄는 파랑으로 — 주 버튼이 어디인지 바로 보이게
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6), Wpf.Ui.Appearance.ApplicationTheme.Dark);
        base.OnStartup(e);
        // 창은 여기서 연다 (StartupUri 를 쓰면 두 번째 실행에서 Shutdown 한 뒤에도 창이 만들어질 수 있다).
        // 창을 만들다 실패하면 알리고 끝낸다 — 창 없이 프로세스만 남으면 잠금(mutex) 때문에 다시 실행해도 열리지 않는다.
        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            Errors.WriteLog(ex, "gui");
            MessageBox.Show($"프로그램을 시작하지 못했습니다.\n\n{Errors.Describe(ex)}\n\n자세한 내용: {Errors.LogPath}",
                            "FFXIV 플러그인 임시 패치", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
                MessageBox.Show($"예상하지 못한 문제가 생겼습니다.\n\n{detail}\n\n자세한 내용: {Errors.LogPath}",
                                "FFXIV 플러그인 임시 패치", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _showing = false; }
        }
        if (noWindow) Current?.Shutdown(1);
    }
}
