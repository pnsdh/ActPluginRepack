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

    [RelayCommand]
    private void OpenRelease()
    {
        string url = _update?.Url ?? AppInfo.ReleasesUrl;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(appDir, AppUpdate.GuiExe))
            {
                UseShellExecute = false,
                ArgumentList = { "--wait-pid", Environment.ProcessId.ToString(), "--updated-from", AppInfo.DisplayVersion },
            });
            // 창 닫기 막음(업데이트 중에는 닫지 못함)에 걸리지 않게 먼저 내린다
            restarting = true;
            Updating = false;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            if (applied) AppUpdate.Revert(appDir); // 바꿔 넣었는데 실행하지 못함 (백신 등) -> 원래 파일로
            Errors.WriteLog(ex, "update");
            Notice = "자동 업데이트를 하지 못했습니다. 지금 버전은 그대로입니다. 다시 시도하거나 [직접 받기]로 받으세요.";
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
