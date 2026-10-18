using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

// 이 프로그램의 새 버전 알림 + 원터치 자동 업데이트. 창 위쪽 띠로 간편/고급 모드 모두에 보인다 (작업 중에는 누를 수 없음).
internal sealed partial class UpdateViewModel : ObservableObject
{
    public UpdateViewModel()
    {
        if (App.UpdatedFrom is { } from)
        {
            UpdatedNotice = $"{AppInfo.DisplayVersion} 버전으로 업데이트했습니다. (이전 {from})";
            ShowUpdated = true;
        }
        _ = CheckAsync();
    }

    [ObservableProperty] private string? _notice;        // 새 버전 / 진행 / 실패 안내 (없으면 띠를 숨김)
    [ObservableProperty] private string? _noticeDetail;  // 실패했을 때 기술적인 원인 (글자에 마우스를 올리면)
    [ObservableProperty] private string? _updatedNotice; // 방금 업데이트함
    [ObservableProperty] private bool _showUpdated;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowUpdateButton)), NotifyPropertyChangedFor(nameof(ShowManualButton))]
    private bool _canAutoUpdate = AppUpdate.Supported(AppContext.BaseDirectory); // 배포본 구성이고 그 폴더에 쓸 수 있을 때만
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowUpdateButton))] private bool _updating;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowManualButton))] private bool _failed;
    public bool ShowUpdateButton => CanAutoUpdate && !Updating;   // 실패한 뒤에도 보여 다시 시도할 수 있게
    public bool ShowManualButton => !CanAutoUpdate || Failed;
    private AppInfo.Update? _update;

    private async Task CheckAsync()
    {
        if (await AppState.UpdateCheck.Value is not { } update) return;
        _update = update;
        Notice = $"새 버전이 나왔습니다: {update.Version.ToString(3)} (지금 {AppInfo.DisplayVersion})";
        // 이 프로그램이 있는 폴더에 쓸 수 없으면(Program Files 등) 자동 업데이트는 받은 뒤에 매번 실패한다 -> 처음부터 직접 받기만
        if (CanAutoUpdate && !await Task.Run(() => Elevation.CanWrite(AppContext.BaseDirectory)))
        {
            CanAutoUpdate = false;
            Notice += ". 이 프로그램이 있는 폴더에 쓸 수 없어 자동으로 업데이트할 수 없습니다. " +
                      "[직접 받기]로 받은 zip 을 바탕화면이나 문서처럼 쓸 수 있는 폴더에 풀어서 쓰세요.";
        }
    }

    // 새 버전이 실제로 시작했다는 신호. 이전 버전은 새 버전을 띄운 뒤 이 신호를 기다리고, 신호 없이 끝나거나(런타임이 맞지 않음 등)
    // 오래 걸리면 원래 파일로 되돌린 채 계속 쓴다 — 고장 난 새 버전만 남지 않게.
    internal static string StartedEventName(int oldPid) => $@"Local\ActPluginRepack.Gui.Started.{oldPid}";

    internal static void SignalStarted(int oldPid)
    {
        if (System.Threading.EventWaitHandle.TryOpenExisting(StartedEventName(oldPid), out var h))
            using (h) h.Set();
    }

    // 새 버전이 시작 신호를 보냈으면 true. 신호 없이 끝났거나(런타임이 맞지 않음 등) 너무 오래 걸리면(런타임 설치 안내 창에서
    // 멈춤 등) 그 프로세스를 끝내고 false — 원래 파일로 되돌린다
    private static bool WaitStarted(System.Diagnostics.Process next, System.Threading.EventWaitHandle started)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (started.WaitOne(200)) return true;
            if (next.HasExited) return started.WaitOne(0);
        }
        try { next.Kill(entireProcessTree: true); next.WaitForExit(5000); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException) { }
        return false;
    }

    [RelayCommand]
    private void OpenRelease()
    {
        string url = _update?.Url ?? AppInfo.ReleasesUrl;
        // 직접 받기가 마지막 길이다 — 브라우저를 열지 못하면 주소라도 보이게
        if (!Shell.Open(url)) Notice = $"브라우저를 열지 못했습니다. 이 주소를 직접 여세요: {url}";
    }

    // 원터치: 받기 -> 확인 -> 바꿔 넣기 -> 새 버전 실행 -> 끝내기. 실패하면 아무것도 바꾸지 않고(또는 되돌리고) 직접 받기를 안내.
    [RelayCommand]
    private async Task UpdateNow()
    {
        if (_update is not { } update || Updating || Navigator.Main.JobRunning) return;
        Updating = true;
        NoticeDetail = null;
        string appDir = AppContext.BaseDirectory;
        bool applied = false, restarting = false;
        try
        {
            Notice = "새 버전을 받는 중...";
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            string staging = await Task.Run(() => AppUpdate.Download(AppUpdate.Resolve(update),
                p => dispatcher.BeginInvoke(() => Notice = $"새 버전을 받는 중... {p:P0}")));
            Notice = "새 버전으로 바꾸는 중...";
            await Task.Run(() => AppUpdate.Apply(staging, appDir));
            applied = true;
            // 끝내기 직전에 다시 확인: 작업이 돌고 있으면 끝내지 않는다 (Shutdown 중에는 창 닫기 막음이 듣지 않아
            // 설치 도중에 끊길 수 있다) -> 아래에서 원래 파일로 되돌린다
            if (Navigator.Main.JobRunning)
                throw new ToolException("작업이 진행 중이라 업데이트를 마치지 못했습니다. 작업이 끝난 뒤 다시 누르세요.");
            // 새 버전이 실제로 시작하는지 본다 (신호를 먼저 만들어 둬야 놓치지 않는다)
            using var started = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset,
                                                                     StartedEventName(Environment.ProcessId));
            using var next = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(appDir, AppUpdate.GuiExe))
            {
                UseShellExecute = false,
                ArgumentList = { "--wait-pid", Environment.ProcessId.ToString(), "--updated-from", AppInfo.DisplayVersion },
            }) ?? throw new ToolException("새 버전을 실행하지 못했습니다.");
            Notice = "새 버전을 시작하는 중...";
            if (!await Task.Run(() => WaitStarted(next, started)))
                throw new ToolException("새 버전이 제대로 시작하지 않아 원래 버전으로 되돌렸습니다. " +
                                        "필요한 .NET 데스크톱 런타임이 없을 수 있습니다. [직접 받기]의 안내를 확인하세요.");
            // 창 닫기 막음(업데이트 중에는 닫지 못함)에 걸리지 않게 먼저 내린다
            restarting = true;
            Updating = false;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            // 바꿔 넣었는데 실행하지 못함 (백신, 새 버전이 시작하지 않음 등) -> 원래 파일로
            bool reverted = !applied || AppUpdate.Revert(appDir) != AppUpdate.RevertResult.Partial;
            Errors.WriteLog(ex, "update");
            Notice = reverted
                ? "자동 업데이트를 하지 못했습니다. 지금 버전은 그대로입니다. 다시 시도하거나 [직접 받기]로 받으세요."
                : "자동 업데이트를 하지 못했고, 원래 파일로 다 되돌리지 못했습니다. 이 프로그램을 다시 시작하면 마저 되돌립니다. " +
                  "그래도 안 되면 [직접 받기]로 받아 새로 푸세요.";
            NoticeDetail = Errors.Describe(ex);
            Failed = true;
            Updating = false;
        }
        finally
        {
            // 예상하지 못한 오류(전역 오류 처리로 넘어감)라도 띠가 '진행 중'으로 멈춰 있지 않게
            if (!restarting && Updating)
            {
                if (applied)
                {
                    try { AppUpdate.Revert(appDir); }
                    catch (Exception ex) when (Errors.Recoverable(ex)) { Errors.WriteLog(ex, "update"); }
                }
                Notice = "자동 업데이트를 하지 못했습니다. 다시 시도하거나 [직접 받기]로 받으세요.";
                Failed = true;
                Updating = false;
            }
        }
    }
}
