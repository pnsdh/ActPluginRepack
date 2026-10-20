using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

internal enum StepState { Pending, Running, Done, Warning, Failed, Skipped }

internal enum ResultKind { None, Success, Info, Warning, Error }

// 진행 화면의 단계 키 (작업이 단계를 만들고 Start / Finish 로 가리킨다)
internal static class StepKeys
{
    public const string Check = "check";     // 간편: 게임과 플러그인 확인
    public const string Data = "data";       // 간편: 이번 업데이트 정보 모으기
    public const string Build = "build";
    public const string Install = "install"; // ACT 에 설치 (공식본 설치, 되돌리기 포함)
    public const string Prepare = "prepare"; // 고급
    public const string Rsv = "rsv";         // 고급
    public const string Hotfix = "hotfix";   // 고급
}

// cancellable: 도는 동안 [취소]를 누를 수 있는지 (ACT 의 파일을 바꾸는 단계는 도중에 멈추지 않으므로 false)
internal sealed partial class StepItem(string key, string name, bool cancellable = true) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public bool Cancellable { get; } = cancellable;
    [ObservableProperty] private StepState _state = StepState.Pending;
    [ObservableProperty] private string? _detail;
}

internal sealed record LogLine(LogKind Kind, string Text);

// 결과 아래의 추가 버튼 (공식 플러그인으로 되돌리기, 폴더 열기, 명령 복사 ...). 누르면 글자를 바꿔 알릴 수 있게 Text 는 바뀐다.
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
        // [자세한 기록]에 보이는 줄을 그대로 작업 기록 파일에도 (작업이 직접 넣는 요약 줄까지)
        _taskLog = OpenTaskLog(title);
        Log.CollectionChanged += (_, e) =>
        {
            foreach (LogLine line in e.NewItems ?? Array.Empty<LogLine>()) _taskLog?.Write(line.Kind, line.Text);
        };
        _ = RunAsync(job);
    }

    private readonly TaskLog? _taskLog;
    private readonly LatestOnly _report = new();

    // 작업 폴더의 logs\ 에 (열지 못하면 기록 없이 — 작업은 그대로)
    private static TaskLog? OpenTaskLog(string title)
    {
        try { return TaskLog.Open(AppState.Workspace, title, $"화면: {(AppState.Settings.Advanced ? "고급" : "간편")} 모드"); }
        catch (Exception ex) when (Errors.IsFileError(ex)) { return null; }
    }

    // 기록 파일 끝에 단계와 결과를 적고 닫는다 (문제 보고용 기록을 묶기 전에)
    private void CloseTaskLog()
    {
        if (_taskLog == null) return;
        foreach (var s in Steps)
            _taskLog.Write(LogKind.Note, $"단계: {s.Name} - {s.State}{(string.IsNullOrEmpty(s.Detail) ? "" : $" ({s.Detail})")}");
        _taskLog.Write(LogKind.Section, $"결과 ({Title}): {ResultKind} - {ResultTitle}");
        if (ResultMessage is { Length: > 0 } message) _taskLog.Write(LogKind.Info, message);
        _taskLog.Dispose();
    }

    // 진행하다 바뀔 수 있다 (공식 최신본으로 바꾸려다 다시 확인해 보니 고쳐야 하는 경우 등)
    [ObservableProperty] private string _title;
    public ObservableCollection<StepItem> Steps { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];
    internal IReporter Reporter { get; }

    // 준비(prepare) 의 큰 단계 이름 -> 이 화면의 단계 키 (작업이 정한다)
    internal Func<string, string?>? MapProgress { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelCommand)), NotifyPropertyChangedFor(nameof(ShowSources))] private bool _isRunning = true;
    // 취소: 누르면 지금 단계가 끝날 때 멈춘다. 취소할 수 없는 단계(설치 — 끝까지 하거나 시작 전)가 도는 동안은 누를 수 없다.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunningText)), NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunningText)), NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _cancelling;
    public string RunningText =>
        Cancelling ? "취소하는 중입니다. 지금 단계가 끝나면 멈춥니다." :
        CanCancel ? "진행 중입니다. 창을 닫지 마세요." :
        "ACT 의 파일을 바꾸는 중입니다. 끝날 때까지 기다리세요.";
    private const string CopyLabel = "복사";
    [ObservableProperty] private string _copyText = CopyLabel;
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

    // 없는 키는 작업 코드의 버그다 (InvalidOperationException 이면 "데이터 형식 오류"로 보이므로 따로)
    public StepItem Step(string key) =>
        Steps.FirstOrDefault(s => s.Key == key) ?? throw new ArgumentException($"진행 화면에 없는 단계입니다: {key}", nameof(key));

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

    // 일찍 끝낼 때 아직 시작하지 않은 단계를 모두 건너뜀으로 (그 이유와 함께). 진행 막대는 다시 계산하지 않는다 —
    // 멈춘 곳에 두어 다 된 것처럼 보이지 않게
    public void SkipRemaining(string detail)
    {
        foreach (var s in Steps.Where(s => s.State == StepState.Pending))
        {
            s.State = StepState.Skipped;
            s.Detail = detail;
        }
    }

    private void UpdateProgress()
    {
        double done = Steps.Count(s => s.State is not (StepState.Pending or StepState.Running));
        double running = Steps.Count(s => s.State == StepState.Running) * 0.5;
        Progress = Steps.Count == 0 ? 0 : (done + running) * 100 / Steps.Count;
        CanCancel = !Steps.Any(s => s.State == StepState.Running && !s.Cancellable);
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
            // 취소로 멈춘 단계는 실패가 아니다
            foreach (var s in Steps.Where(s => s.State == StepState.Running))
            {
                s.State = ex is OperationCanceledException ? StepState.Skipped : StepState.Failed;
                if (ex is OperationCanceledException) s.Detail = "취소함";
            }
            foreach (var s in Steps.Where(s => s.State == StepState.Pending)) s.State = StepState.Skipped;
            var (kind, title, message, expandLog) = Friendly.Classify(ex);
            SetResult(kind, title, message);
            Log.Add(new LogLine(LogKind.Error, Errors.Describe(ex)));
            if (ex is not (ToolException or OperationCanceledException))
            {
                Errors.WriteLog(ex, Title);
                _taskLog?.Raw(ex.ToString());
            }
            if (expandLog) ShowLog = true;
        }
        finally
        {
            foreach (var s in Steps.Where(s => s.State == StepState.Pending)) s.State = StepState.Skipped;
            // 실패·취소했거나 실패·경고로 끝난 단계가 있으면 진행 막대는 멈춘 곳에 둔다 (다 된 것처럼 보이지 않게)
            if (completed && Steps.All(s => s.State is not (StepState.Failed or StepState.Warning))) Progress = 100;
            IsRunning = false;
            CanCancel = false;
            CloseTaskLog();
            // 실패·경고로 끝났으면 결과 아래에서 바로 보낼 기록을 만들 수 있게 (안내는 결과 버튼 아래에)
            // (안내가 가리키는 [디스코드]도 바로 옆에)
            if (ResultKind is ResultKind.Error or ResultKind.Warning)
            {
                AddAction(Titles.SaveReport, Wpf.Ui.Controls.SymbolRegular.FolderZip24,
                          _ => ProblemReport.Run(_report, note => ActionNote = note));
                AddAction(Titles.Discord, Wpf.Ui.Controls.SymbolRegular.Chat24, _ =>
                {
                    ProblemReport.OpenDiscord(note => ActionNote = note);
                    return Task.CompletedTask;
                });
            }
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
    private static bool TryCopy(string text)
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

    // prepare 를 마쳐 출처 보고가 새로 생겼으면 [출처] (돌아오면 이 결과 화면으로). 작업이 다 끝난 뒤에만 보인다 — 도는 동안 다른 화면으로
    // 가면 작업 중 보호(창 닫기·자동 업데이트·다른 작업 시작 막기)가 이 화면을 기준으로 하므로 풀린다
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSources))] private bool _hasSources;
    public bool ShowSources => HasSources && !IsRunning;

    [RelayCommand]
    private void OpenSources() => Navigator.Show(new SourcesViewModel(() => Navigator.Show(this)));

    // 복사하고 버튼 글자로 2초 동안 결과를 알린 뒤 label 로 되돌린다
    public static async Task CopyWithFeedback(string text, string label, Action<string> show)
    {
        show(TryCopy(text) ? "복사했습니다" : "복사하지 못했습니다. 다시 누르세요");
        await Task.Delay(2000);
        show(label);
    }

    [RelayCommand]
    private Task CopyLog() =>
        CopyWithFeedback(string.Join(Environment.NewLine, Log.Select(l => l.Text)), CopyLabel, t => CopyText = t);

}
