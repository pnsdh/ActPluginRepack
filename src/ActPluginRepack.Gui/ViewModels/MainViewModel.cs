using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

// 창 하나에 화면(페이지) 하나. 화면 전환은 Navigator 로.
internal sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Navigator.Main = this;
        _isAdvanced = AppState.Settings.Advanced;
        // 자동 업데이트 중에는 작업을 시작하거나 다른 화면으로 가지 못하게 (업데이트가 끝나면 프로그램을 다시 띄우므로)
        Update.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UpdateViewModel.Updating)) return;
            OnPropertyChanged(nameof(CanNavigate));
            OnPropertyChanged(nameof(PageEnabled));
        };
        Navigator.ShowStart();
    }

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private bool _isAdvanced;

    // 창 위쪽의 새 버전 / 자동 업데이트 띠 (모든 화면 공통)
    public UpdateViewModel Update { get; } = new();

    // 진행 중이거나 자동 업데이트 중에는 다른 화면으로 못 가게
    public bool CanNavigate => CurrentPage is not RunViewModel { IsRunning: true } && !Update.Updating;

    // 화면 내용(버튼들): 자동 업데이트 중에는 누를 수 없게 (작업 중에는 업데이트 띠가 꺼져 있어 둘이 겹치지 않는다)
    public bool PageEnabled => !Update.Updating;

    // 작업이 진행 중인지 (자동 업데이트가 프로그램을 끝내기 직전에 다시 확인한다)
    public bool JobRunning => CurrentPage is RunViewModel { IsRunning: true };

    partial void OnCurrentPageChanged(object? value)
    {
        // (출처 화면에서 진행 화면으로 돌아올 때 같은 화면이 다시 오므로 한 번만 붙인다)
        if (value is RunViewModel run && _watched.TryAdd(run, new object()))
            run.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RunViewModel.IsRunning)) OnPropertyChanged(nameof(CanNavigate));
            };
        OnPropertyChanged(nameof(CanNavigate));
    }

    // 감시를 붙인 진행 화면 (지나간 화면은 붙잡지 않게 약한 참조)
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<RunViewModel, object> _watched = new();

    partial void OnIsAdvancedChanged(bool value)
    {
        AppState.Settings.Advanced = value;
        // 저장 실패(백신 잠금 등)로 모드 전환까지 막지 않는다 — 바인딩 setter 안의 예외는 WPF 가 조용히 삼켜 화면만 안 바뀐다
        try { AppState.Settings.Save(); }
        catch (Exception ex) when (Errors.IsToolOrFileError(ex)) { Errors.WriteLog(ex, "gui settings"); }
        if (!CanNavigate) return;
        if (CurrentPage is HomeViewModel or AdvancedViewModel) Navigator.ShowStart();
        // 설정 화면은 모드에 따라 보이는 항목과 저장하는 항목이 다르므로 새 모드로 다시 연다 (저장하지 않은 입력은 버림)
        else if (CurrentPage is SettingsViewModel) Navigator.Show(new SettingsViewModel());
    }

    [RelayCommand]
    private void GoHome() => Navigator.ShowStart();

    [RelayCommand]
    private void GoSettings() => Navigator.Show(new SettingsViewModel());

    [RelayCommand]
    private void GoHelp() => Navigator.Show(new HelpViewModel());
}

internal static class Navigator
{
    public static MainViewModel Main { get; set; } = null!;

    public static void Show(object page) => Main.CurrentPage = page;

    // 고급 모드 화면은 실행마다 새로 만들지 않고 이 실행 동안 하나만 쓴다 (고른 작업과 옵션이 유지되게)
    private static AdvancedViewModel? _advanced;

    // 처음 화면: 게임 클라이언트가 없으면 설정 마법사, 아니면 모드에 맞는 시작 화면.
    // config.json 을 읽지 못한 것(깨진 파일)은 "설정 없음"이 아니다 — 시작 화면이 그 문제와 [게임 위치 설정]을 보여 준다.
    public static void ShowStart()
    {
        var (config, error) = AppState.TryLoadConfig();
        if (error == null && config.Clients.Count == 0 && config.ClientErrors.Count == 0) Show(new SetupViewModel(firstRun: true));
        else if (Main.IsAdvanced)
        {
            if (_advanced == null) _advanced = new AdvancedViewModel();
            else _advanced.Reload();
            Show(_advanced);
        }
        else Show(new HomeViewModel());
    }

    // 시작 화면에 한 번 보일 알림.
    // 간편 모드: 알림 칸 — 다른 화면으로 갔다 오면(첫 화면을 새로 만든다) 사라진다. 창으로 돌아올 때의 다시 읽기(Reload)는
    //   남기고, [새로 고침](주 버튼을 누를 수 없을 때만 보임)을 누르면 지운다.
    // 고급 모드: 현재 상태 칸 맨 위 — 상태를 다시 읽으면(새로 고침, 다른 화면에서 돌아옴) 사라진다.
    public static void Notify(string message)
    {
        switch (Main.CurrentPage)
        {
            case HomeViewModel home:
                home.Info = message;
                break;
            case AdvancedViewModel advanced:
                advanced.StatusText = $"{message}\n\n{advanced.StatusText}";
                break;
        }
    }

    // 자동 업데이트 중이거나 다른 작업이 진행 중이면 시작하지 않는다 (버튼은 꺼져 있지만 키보드 등으로 들어와도)
    public static void ShowRun(string title, IEnumerable<StepItem> steps, Func<RunViewModel, CancellationToken, Task> job)
    {
        if (Main.Update.Updating || Main.JobRunning) return;
        Show(new RunViewModel(title, steps, job, ShowStart));
    }

    // ACT 의 파일을 바꾸는 작업: 진행 화면으로 넘어가기 전에 ACT·게임이 꺼져 있는지 확인한다 (넘어간 뒤에 실패하면 누른
    // 버튼이 있던 화면으로 돌아올 수 없다). 켜져 있으면 넘어가지 않고 그 안내를 onBlocked 로 — 누른 화면에 보인다.
    public static void TryShowRun(string title, IEnumerable<StepItem> steps, Func<RunViewModel, CancellationToken, Task> job,
                                  Action<string> onBlocked)
    {
        if (Main.Update.Updating || Main.JobRunning) return;
        try { Install.EnsureClosed(); }
        catch (AppsRunningException ex)
        {
            onBlocked(Friendly.Describe(ex).Message);
            return;
        }
        ShowRun(title, steps, job);
    }
}
