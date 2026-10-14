using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

internal enum StepState { Pending, Running, Done, Warning, Failed, Skipped }

internal enum ResultKind { None, Success, Info, Warning, Error }

internal sealed partial class StepItem(string key, string name) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    [ObservableProperty] private StepState _state = StepState.Pending;
    [ObservableProperty] private string? _detail;
}

internal sealed record LogLine(LogKind Kind, string Text);

// 결과 아래의 추가 버튼 (설치 전으로 되돌리기, 폴더 열기, 명령 복사 ...). 누르면 글자를 바꿔 알릴 수 있게 Text 는 바뀐다.
internal sealed partial class ResultAction(string text, Wpf.Ui.Controls.SymbolRegular icon, Func<ResultAction, Task> run) : ObservableObject
{
    [ObservableProperty] private string _text = text;
    public Wpf.Ui.Controls.SymbolRegular Icon { get; } = icon;

    [RelayCommand]
    private Task Run() => run(this);
}

// 진행 화면: 단계 목록, 진행 막대, 기록, 결과. 작업(job)은 이 모델을 고쳐 가며 진행 상황을 알린다.
internal sealed partial class RunViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action _onClose;

    public RunViewModel(string title, IEnumerable<StepItem> steps, Func<RunViewModel, CancellationToken, Task> job, Action onClose)
    {
        _title = title;
        foreach (var s in steps) Steps.Add(s);
        _onClose = onClose;
        Reporter = new UiReporter((kind, text) => Log.Add(new LogLine(kind, text)), OnProgress);
        _ = RunAsync(job);
    }

    // 진행하다 바뀔 수 있다 (공식 최신본으로 바꾸려다 다시 확인해 보니 고쳐야 하는 경우 등)
    [ObservableProperty] private string _title;
    public ObservableCollection<StepItem> Steps { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];
    internal IReporter Reporter { get; }

    // 준비(prepare) 의 큰 단계 이름 -> 이 화면의 단계 키 (작업이 정한다)
    internal Func<string, string?>? MapProgress { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool _isRunning = true;
    // 취소: 누르면 지금 단계가 끝날 때 멈춘다. 설치/되돌리기는 도중에 멈추지 않으므로(끝까지 하거나 시작 전) 그동안은 누를 수 없다.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunningText)), NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunningText)), NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _cancelling;
    public string RunningText =>
        Cancelling ? "취소하는 중입니다. 지금 단계가 끝나면 멈춥니다." :
        CanCancel ? "진행 중입니다. 창을 닫지 마세요." :
        "ACT 의 파일을 바꾸는 중입니다. 끝날 때까지 기다리세요.";
    [ObservableProperty] private string _copyText = "복사";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _showLog;
    [ObservableProperty] private ResultKind _resultKind;
    [ObservableProperty] private string? _resultTitle;
    [ObservableProperty] private string? _resultMessage;
    public ObservableCollection<ResultAction> Actions { get; } = [];
    // 결과 버튼을 눌렀지만 할 수 없을 때의 안내 (버튼 글자는 줄바꿈되지 않으므로 버튼 아래에 따로 보인다)
    [ObservableProperty] private string? _actionNote;

    public void AddAction(string text, Wpf.Ui.Controls.SymbolRegular icon, Func<ResultAction, Task> run) =>
        Actions.Add(new ResultAction(text, icon, run));

    public StepItem Step(string key) => Steps.First(s => s.Key == key);

    public void Start(string key)
    {
        Step(key).State = StepState.Running;
        UpdateProgress();
    }

    public void Finish(string key, StepState state = StepState.Done, string? detail = null)
    {
        var s = Step(key);
        s.State = state;
        if (detail != null) s.Detail = detail;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        double done = Steps.Count(s => s.State is not (StepState.Pending or StepState.Running));
        double running = Steps.Count(s => s.State == StepState.Running) * 0.5;
        Progress = Steps.Count == 0 ? 0 : (done + running) * 100 / Steps.Count;
        CanCancel = !Steps.Any(s => s.State == StepState.Running && s.Key is "install" or "restore");
    }

    private void OnProgress(string stage, int index, int count)
    {
        if (MapProgress?.Invoke(stage) is not { } key) return;
        foreach (var s in Steps.TakeWhile(s => s.Key != key).Where(s => s.State == StepState.Running)) s.State = StepState.Done;
        if (Step(key).State == StepState.Pending) Start(key);
    }

    public void SetResult(ResultKind kind, string title, string message)
    {
        ResultKind = kind;
        ResultTitle = title;
        ResultMessage = message;
    }

    private async Task RunAsync(Func<RunViewModel, CancellationToken, Task> job)
    {
        bool completed = false;
        try
        {
            await job(this, _cts.Token);
            completed = true;
        }
        catch (Exception ex)
        {
            foreach (var s in Steps.Where(s => s.State == StepState.Running)) s.State = StepState.Failed;
            foreach (var s in Steps.Where(s => s.State == StepState.Pending)) s.State = StepState.Skipped;
            var (title, message) = Friendly.Describe(ex);
            SetResult(ex is OperationCanceledException ? ResultKind.Info : ResultKind.Error, title, message);
            Log.Add(new LogLine(LogKind.Error, Errors.Describe(ex)));
            if (ex is not (ToolException or OperationCanceledException)) Errors.WriteLog(ex, Title);
            // 기술적인 원인은 기록에 있으니 실패하면 기록을 펼쳐 둔다 (할 일이 분명한 예상된 결과는 빼고)
            if (ex is not (OperationCanceledException or AppsRunningException or ElevationCanceledException or NetworkException or GitHubLimitException
                           or ThrottledException or WorkspaceBusyException or GameOutdatedException))
                ShowLog = true;
        }
        finally
        {
            foreach (var s in Steps.Where(s => s.State == StepState.Pending)) s.State = StepState.Skipped;
            // 실패·취소했거나 실패·경고로 끝난 단계가 있으면 진행 막대는 멈춘 곳에 둔다 (다 된 것처럼 보이지 않게)
            if (completed && Steps.All(s => s.State is not (StepState.Failed or StepState.Warning))) Progress = 100;
            IsRunning = false;
            CanCancel = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelNow))]
    private void Cancel()
    {
        Cancelling = true;
        _cts.Cancel();
    }

    private bool CanCancelNow() => IsRunning && CanCancel && !Cancelling;

    // 다른 프로그램이 클립보드를 잡고 있으면 실패한다 (COMException) — 앱 오류로 보내지 않고 false
    public static bool TryCopy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    [RelayCommand]
    private void Close() => _onClose();

    [RelayCommand]
    private void ToggleLog() => ShowLog = !ShowLog;

    [RelayCommand]
    private async Task CopyLog()
    {
        CopyText = TryCopy(string.Join(Environment.NewLine, Log.Select(l => l.Text))) ? "복사했습니다" : "복사하지 못했습니다. 다시 누르세요";
        await Task.Delay(2000);
        CopyText = "복사";
    }

}
