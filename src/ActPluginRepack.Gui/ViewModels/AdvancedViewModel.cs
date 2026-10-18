using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

internal sealed partial class SourceOption(string name, string label, string group, string description) : ObservableObject
{
    public string Name { get; } = name;
    public string Label { get; } = label;
    public string Group { get; } = group;
    public string Description { get; } = description;
    [ObservableProperty] private bool _isChecked = true;
}

// 고급 모드: 작업과 옵션을 골라 실행. CLI 의 prepare / build / install 과 같은 선택지.
internal sealed partial class AdvancedViewModel : ObservableObject
{
    public AdvancedViewModel()
    {
        LoadConfigOptions();
        _idaTarget = _idaTargetDefault;
        foreach (var source in SourceOptions()) Sources.Add(source);
        RefreshStatus();
    }

    // 데이터 소스의 화면 이름과 설명. 분류 안에서는 이 순서(우선순위)로 보인다.
    private static readonly (string Name, string Label, string Description)[] SourceTexts =
    [
        ("yarukon", "Yarukon",
         "Yarukon/FFXIVNetworkOpcodes 가 CN 클라이언트에서 IDA 로 추출해 공개한 opcode 입니다. 게임 버전 날짜로 맞춥니다(CN/글로벌/한국 공통). 1순위."),
        ("karashiiro", "karashiiro",
         "karashiiro/FFXIVOpcodes 의 지역별 opcode 입니다(대만판 포함). 이번 버전용임이 확인되면(패치 번호, 또는 지역 파일이 이번 출시 뒤에 갱신됨) 2순위, 확인할 수 없으면(대만판 등) 다른 출처가 없을 때만 씁니다."),
        ("localdiff", "로컬 opcodediff",
         "지금 게임 exe 와 직전 출시 exe(공식 패치 서버에서 받음)를 opcodediff 로 맞대 공식 플러그인 값을 바꿉니다(클라이언트당 약 5초). 패치 번호가 필요 없어 공개 자료가 나오기 전(출시 직후)에도 됩니다. 항상 돌려 공개본과 대조하고, 다르면 공개본을 씁니다(큰 패치에서 가끔 틀림). 스크립트·radare2·Python 패키지는 자동으로 받고, Python 3.12 이상이 없으면 휴대용 Python(약 14MB)도 받아 씁니다."),
        ("opcodediff", "opcodediff diff",
         "xivdev/opcodediff 가 공개한 '직전 버전 → 이번 버전' opcode 대응표를 직전 목록에 적용합니다. 로컬 opcodediff 와 같은 스크립트의 결과라, 로컬 opcodediff 를 못 돌렸을 때 씁니다."),
        ("ida", "IDA",
         "IDA 로 exe 를 분석해 직접 추출합니다(약 5분, 기본은 같은 버전 중국판 exe 한 번). IDA 9 이상이 필요하며, 앞의 출처가 모두 없을 때 씁니다(간편 모드에서는 쓰지 않음)."),
        ("oxidizer", "xiv-data-oxidizer",
         "설치된 클라이언트에서 직접 추출합니다(git + Rust 필요, 설정에서 켜야 함). 결과는 SharpVision 과 같습니다."),
        ("sharpvision", "SharpVision",
         "설치된 클라이언트에서 직접 추출합니다. 설정에 경로가 없으면 최신 릴리스를 받아 씁니다."),
        ("datamining", "datamining 저장소",
         "xivapi / Ra-Workspace / thewakingsands 의 공개 CSV 입니다. 커밋 메시지로 게임 버전을 확인합니다."),
        ("xivdata", "fae xiv-data",
         "fae-industries/xiv-data 의 공개 추출본입니다. 패치 당일 수동으로 올라와 늦을 수 있어 가장 뒤 순위입니다."),
        ("upstream", "upstream deucalion",
         "deucalion 후크 시그니처(Recv / Send / SendLobby / CreateTarget)가 깨졌을 때, deucalion 원본 저장소(ff14wed/deucalion)에 이미 반영된 수정을 찾아 씁니다. main 브랜치 소스의 상수 → 최신 릴리스 dll 순서이며, 설정된 모든 클라이언트에서 검증을 통과해야 씁니다. 7.57 때는 공식 플러그인보다 약 5시간 빨랐습니다. 1순위."),
        ("sigregen", "SigRegen",
         "원래 패턴이 맞던 옛 exe 와 지금 exe 를 비교해 시그니처를 다시 만듭니다(모든 종류). 옛 exe 는 공식 패치 서버에서 받습니다. 2순위."),
    ];

    // 데이터 소스 목록은 Core 의 출처 이름(prepare --sources 와 같은 것)으로 만든다 — Core 에 새 출처가 생겨도 화면에서 빠지지 않게
    // (설명이 없는 것은 이름만, 그 분류의 끝에). Core 에 없는 이름은 보이지 않는다.
    private static IEnumerable<SourceOption> SourceOptions()
    {
        foreach (var (group, names) in new[] { ("opcode", OpcodeCollector.SourceNames), ("CSV", CsvCollector.SourceNames), ("시그니처", SignatureRepair.Sources) })
        {
            int Rank(string name) => Array.FindIndex(SourceTexts, s => s.Name == name) is var i and >= 0 ? i : int.MaxValue;
            foreach (string name in names.OrderBy(Rank))
            {
                int rank = Rank(name);
                yield return rank == int.MaxValue
                    ? new SourceOption(name, name, group, $"{group} 출처 '{name}' 입니다.")
                    : new SourceOption(name, SourceTexts[rank].Label, group, SourceTexts[rank].Description);
            }
        }
    }

    // 다시 보일 때 (실행을 마치고 돌아옴, 설정을 바꾸고 돌아옴): 고른 작업과 옵션은 그대로 두고 설정과 상태만 다시 읽는다
    public void Reload()
    {
        Notice = null;
        string oldIdaDefault = _idaTargetDefault;
        LoadConfigOptions();
        OnPropertyChanged(nameof(ShowHotfix));
        if (!ShowHotfix) DoHotfix = false; // 설정에서 배포 기능을 껐으면 보이지 않는 선택이 남지 않게
        // 쓸 수 없게 된 도구(설정에서 끔, 경로를 지움)의 "우선" 선택은 꺼진 칸에 체크된 채 남지 않게 끈다
        if (!HasIda) ForceIda = false;
        // IDA 분석 대상을 직접 바꾸지 않았으면(옛 기본값 그대로) 설정의 새 기본값을 따른다
        if (IdaTarget == oldIdaDefault) IdaTarget = _idaTargetDefault;
        RefreshStatus();
    }

    // 설정(config.json)에 따라 달라지는 것: 직접 추출 도구를 쓸 수 있는지, IDA 분석 대상 기본값
    private void LoadConfigOptions()
    {
        var config = AppState.LoadConfig();
        HasIda = config.Ida != null;
        _idaTargetDefault = config.IdaTarget;
        HasLocalDiff = config.LocalDiff && LocalDiff.HasPython(config.Python);
        DirectExtractHint = string.Join(" ", new[]
        {
            HasIda ? null : "IDA 설치 폴더를 찾지 못했습니다(Program Files 의 IDA 9 이상을 자동으로 찾습니다).",
            HasLocalDiff ? null : config.LocalDiff
                ? "설정에 적은 Python 경로가 없어 로컬 opcodediff 를 쓸 수 없습니다."
                : "로컬 opcodediff 가 설정에서 꺼져 있습니다.",
        }.Where(x => x != null)) is { Length: > 0 } hint ? hint : null;
    }

    // 작업 (하나도 고르지 않으면 [선택한 작업 실행]을 누를 수 없다)
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _updatePlugin = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _sigcheck = true;
    [ObservableProperty] private bool _repair = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _opcodes = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _csv = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _doRsv;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _doBuild = true;
    [ObservableProperty] private bool _buildCsv = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InstallOverlayEnabled)), NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _doInstall;
    // 설치 옵션: 고친 플러그인 대신 공식 최신본(input\plugin), OverlayPlugin opcode 도 넣기 (공식본 설치는 OverlayPlugin 을 건드리지 않음)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InstallOverlayEnabled))] private bool _installOfficial;
    [ObservableProperty] private bool _installOverlay = true;
    public bool InstallOverlayEnabled => DoInstall && !InstallOfficial;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _doHotfix;
    public bool ShowHotfix => AppState.Settings.Hotfix; // 설정의 "고친 플러그인 배포 기능"을 켰을 때만

    // 옵션
    [ObservableProperty] private bool _forceIda;
    // IDA 로 분석할 exe 의 지역 (통합 클라이언트끼리 opcode 가 같으므로 한 번만 분석)
    [ObservableProperty] private string _idaTarget;
    private string _idaTargetDefault = AppConfig.DefaultIdaTarget;
    // 고를 수 있는 것: opcode 가 같은 통합 클라이언트 지역 (기본값 먼저)
    public IReadOnlyList<KeyValuePair<string, string>> IdaTargets { get; } =
        Region.All.Where(r => r.CnOpcodes)
            .OrderBy(r => r.Name == AppConfig.DefaultIdaTarget ? 0 : 1)
            .Select(r => new KeyValuePair<string, string>(r.Name, $"{r.Label} exe{(r.Name == AppConfig.DefaultIdaTarget ? " (기본)" : "")}"))
            .ToList();
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private bool _hasIda;
    [ObservableProperty] private bool _hasLocalDiff;
    // 직접 추출을 못 고르는 이유 (도구를 찾지 못함 / 꺼 둠)
    [ObservableProperty] private string? _directExtractHint;
    public ObservableCollection<SourceOption> Sources { get; } = [];

    [ObservableProperty] private string _statusText = "";
    // 실행 버튼을 누르기 전에 막힌 이유 (ACT 실행 중 등 — 없으면 숨김)
    [ObservableProperty] private string? _notice;

    public bool AnyPrepare => UpdatePlugin || Sigcheck || Opcodes || Csv;
    private bool AnySelected => AnyPrepare || DoRsv || DoBuild || DoHotfix || DoInstall;

    // 모두 체크돼 있으면 제한 없음(빈 목록), 아니면 체크된 것만
    public IReadOnlyCollection<string> SelectedSources =>
        Sources.All(s => s.IsChecked) ? [] : Sources.Where(s => s.IsChecked).Select(s => s.Name).ToList();

    [RelayCommand]
    private void ResetDefaults()
    {
        UpdatePlugin = Sigcheck = Repair = Opcodes = Csv = DoBuild = BuildCsv = InstallOverlay = true;
        DoRsv = DoInstall = InstallOfficial = DoHotfix = ForceIda = Overwrite = false;
        IdaTarget = _idaTargetDefault;
        foreach (var s in Sources) s.IsChecked = true;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(GitHubTip))] private string _gitHubText = "GitHub API 확인 중...";
    // 줄여 보이는 GitHubText 의 전체 + 설명
    public string GitHubTip => GitHubText + $"\n\napi.github.com 사용량입니다. 토큰이 없으면 IP 당 시간당 {GitHub.AnonymousLimit}회지만, 이때는 웹 페이지로 확인해서 보통 쓰지 않습니다. " +
                               $"토큰이 있으면 API 를 먼저 씁니다(시간당 {GitHub.TokenLimit}회). 설정에서 토큰을 등록할 수 있습니다(GitHub CLI 로 로그인돼 있으면 자동으로 씁니다).";

    [RelayCommand]
    private void RefreshStatus()
    {
        // 작업 폴더나 ACT 파일을 읽지 못해도(권한, 잠긴 파일 등) 화면이 오류로 멈추지 않고 그 이유를 상태 칸에 보인다
        try
        {
            var ws = AppState.Workspace;
            var s = Status.Collect(ws);
            // 고정폭 글꼴(D2Coding) — 이름표는 CLI 의 status 와 같은 칸 수로
            var lines = new List<string> { StatusLine.Format("작업 폴더", ws.Root) };
            if (AppState.WorkspaceFallback is { } fallback) lines.Add(StatusLine.Format("", fallback));
            lines.AddRange(s.Describe().Select(l => l.Format()));
            lines.AddRange(s.Advice.Select(a => $"=> {a}"));
            StatusText = string.Join("\n", lines);
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            StatusText = $"!! 상태를 읽지 못했습니다: {Errors.Describe(ex)}\n" +
                         "   잠시 후 [새로 고침](↻ 버튼)을 누르세요." +
                         (AppState.WorkspaceFallback is { } fallback ? $"\n   {fallback}" : "");
        }
        _ = RefreshGitHubAsync();
    }

    // GitHub API 사용량 (네트워크라 화면을 막지 않게 따로. [새로 고침]을 여러 번 눌렀으면 마지막 것만)
    private Task RefreshGitHubAsync()
    {
        const string label = "GitHub API  ";
        GitHubText = "GitHub API 확인 중...";
        return _gitHubCheck.Run(() => label + GitHub.TryDescribeUsage(brief: true).Text,
                                ex => label + GitHub.UsageUnknown(ex, brief: true), "gui github", text => GitHubText = text);
    }

    private readonly LatestOnly _gitHubCheck = new();

    // ACT 의 파일을 바꾸는 단계(설치)를 골랐을 때만 넘어가기 전에 ACT·게임이 꺼져 있는지 확인한다
    // (prepare·build·hotfix 만이면 ACT 가 켜져 있어도 된다)
    [RelayCommand(CanExecute = nameof(AnySelected))]
    private void Run()
    {
        if (!AnySelected) return;
        Notice = null;
        var steps = Jobs.SelectedSteps(this).ToList();
        Func<RunViewModel, CancellationToken, Task> job = (vm, ct) => Jobs.RunSelected(this, vm, ct);
        if (DoInstall) Navigator.TryShowRun(Titles.Selected, steps, job, n => Notice = n);
        else Navigator.ShowRun(Titles.Selected, steps, job);
    }

    [RelayCommand]
    private void Restore()
    {
        Notice = null;
        Navigator.TryShowRun(Titles.Restore, Jobs.RestoreSteps(), Jobs.Restore, n => Notice = n);
    }

    [RelayCommand]
    private void OpenSettings() => Navigator.Show(new SettingsViewModel());

    [RelayCommand]
    private void OpenWorkspace() => Shell.Open(AppState.Workspace.Root);
}
