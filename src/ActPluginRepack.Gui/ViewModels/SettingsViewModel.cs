using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

// 설정: 진행 방식, 작업 폴더, 게임 위치, 선택 도구 (고급)
internal sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(string? message = null)
    {
        var (c, error) = AppState.TryLoadConfig();
        _configError = error;
        _rootLoaded = AppState.Workspace.Root;
        _settingErrors = c.SettingErrors.Keys.ToList();
        _message = message ?? (IsAdvanced && c.SettingErrors.Count > 0
            ? $"설정 파일({Config.FileName})의 다음 항목은 값이 틀려 쓰지 않습니다. [저장]을 누르면 지웁니다.\n" + string.Join("\n", c.SettingErrors.Values)
            : null);
        WorkspaceFallback = AppState.WorkspaceFallback;
        _workspace = AppState.Settings.Workspace ?? "";
        _oxidizer = _oxidizerLoaded = c.Oxidizer != null;
        _sharpVision = c.SharpVision ?? "";
        // IDA: 자동으로 찾은 경로는 입력칸이 아니라 안내로 보여 준다 (입력칸은 직접 지정한 것만)
        _ida = _idaLoaded = c.IdaDetected ? "" : c.Ida ?? "";
        IdaPlaceholder = c.IdaDetected
            ? c.Ida != null ? $"자동으로 찾음: {c.Ida}" : "찾지 못했습니다. idat.exe 가 있는 폴더를 지정하세요"
            : c.Ida == null ? "사용하지 않음 (config.json 의 \"ida\": false)" : "비우면 Program Files 에서 자동으로 찾습니다";
        _localDiff = c.LocalDiff;
        _python = c.Python ?? "";
        Games = error ?? (string.Join("\n", c.Clients.Select(x => $"{Region.LabelOf(x.Region)}  {x.ExePath}")
            .Concat(c.ClientErrors)) is { Length: > 0 } g ? g : "(설정 없음)");
        // 고급 설정에만 보이는 것 (간편 모드에서는 확인하지 않는다)
        if (IsAdvanced)
        {
            _ = RefreshPythonAsync(c.Python);
            _ = RefreshGitHubAsync();
        }
    }

    // 쓸 Python (py 런처를 실행하므로 화면을 막지 않게 따로. 그 사이 다시 확인했으면 늦게 끝난 옛 결과로 덮지 않는다)
    private Task RefreshPythonAsync(string? python)
    {
        if (!global::LocalDiff.HasPython(python))
        {
            _pythonCheck.Begin(); // 앞서 시작한 확인의 결과로 덮지 않게
            PythonStatus = "아래에 적은 python.exe 가 없습니다. 고치거나 비우세요.";
            return Task.CompletedTask;
        }
        PythonStatus = "Python 확인 중...";
        return _pythonCheck.Run(
            () => global::LocalDiff.InstalledPython(python) is { } installed
                ? $"설치된 Python 을 씁니다: {installed}"
                : "설치된 Python 3.12 이상이 없어, 필요할 때 휴대용 Python(약 14MB)을 받아 씁니다.",
            ex => $"설치된 Python 을 확인하지 못했습니다. 필요할 때 휴대용 Python(약 14MB)을 받아 씁니다. ({Errors.Describe(ex)})",
            "gui python", status => PythonStatus = status);
    }

    private readonly LatestOnly _pythonCheck = new(), _gitHubCheck = new();

    // 이 화면을 연 때의 모드 (모드를 바꾸면 MainViewModel 이 이 화면을 새로 연다 — 보이는 항목과 저장하는 항목이 같게)
    public bool IsAdvanced { get; } = Navigator.Main.IsAdvanced;
    // Esc 로 돌아가기는 입력칸이 없는 간편 모드에서만 (고급 모드에서 입력칸을 지우려고 누른 Esc 로 고친 내용을 잃지 않게)
    public bool EscGoesBack => !IsAdvanced;
    private readonly string? _configError; // config.json 을 읽지 못함 -> 아래 도구 설정은 기본값이라 저장하지 않는다
    private readonly List<string> _settingErrors; // 값이 틀려 쓰지 않는 항목 — 이 화면에서 쓰지 않는 것도 저장할 때 지운다
    private readonly string _rootLoaded;   // 이 화면의 도구 설정을 읽어 온 작업 폴더
    public string Games { get; }
    public string DefaultWorkspace => AppPaths.UserRoot;
    // 설정한 작업 폴더가 없어져 기본 폴더를 쓰는 중이면 그 안내 (간편 모드에도 [기본 폴더로 되돌리기]와 함께 보인다)
    public string? WorkspaceFallback { get; }

    [ObservableProperty] private string _workspace;
    [ObservableProperty] private bool _oxidizer;
    private readonly bool _oxidizerLoaded; // 직접 관리하는 clone 경로(문자열)는 켜고 끄기를 바꿀 때만 덮어쓴다
    [ObservableProperty] private string _sharpVision;
    [ObservableProperty] private string _ida;
    private readonly string _idaLoaded;
    public string IdaPlaceholder { get; }
    [ObservableProperty] private string _pythonStatus = "Python 확인 중...";
    [ObservableProperty] private bool _localDiff;
    [ObservableProperty] private bool _hotfix = AppState.Settings.Hotfix;
    [ObservableProperty] private string _python;
    [ObservableProperty] private string? _message;

    // ---- GitHub 토큰 (작업 폴더와 상관없이 이 PC 에 저장, 저장 버튼과 별개로 바로 적용) ----
    [ObservableProperty] private string _gitHubStatus = "확인 중...";
    [ObservableProperty] private string _gitHubToken = "";
    [ObservableProperty] private bool _hasSavedToken = GitHub.HasSavedToken;

    // 토큰 설명의 한도 (Core 의 값으로)
    public string GitHubTokenHelp =>
        $"릴리스·커밋 확인은 토큰이 없으면 GitHub 웹 페이지로 해서 API 한도(IP 당 시간당 {GitHub.AnonymousLimit}회)를 거의 쓰지 않습니다. " +
        $"토큰이 있으면 형식이 약속된 API 를 먼저 쓰고(시간당 {GitHub.TokenLimit}회), 실패하면 웹 페이지로 넘어갑니다. " +
        "공개 저장소만 읽으므로 권한을 하나도 주지 않은 토큰이면 충분합니다. 토큰은 이 PC 에 Windows 사용자 계정으로 암호화해 저장하며, " +
        "환경 변수 GITHUB_TOKEN 이나 GitHub CLI(gh) 로그인이 있으면 그것을 먼저 씁니다.";

    // 토큰을 저장하며 다시 확인했으면 먼저 시작한 옛 결과로 덮지 않는다
    private Task RefreshGitHubAsync()
    {
        GitHubStatus = "확인 중...";
        return _gitHubCheck.Run(() => GitHub.TryDescribeUsage().Text, ex => GitHub.UsageUnknown(ex), "gui github", status =>
        {
            GitHubStatus = status;
            HasSavedToken = GitHub.HasSavedToken;
        });
    }

    [RelayCommand]
    private async Task SaveGitHubToken()
    {
        if (string.IsNullOrWhiteSpace(GitHubToken))
        {
            Message = "저장할 GitHub 토큰을 먼저 입력하세요.";
            return;
        }
        try
        {
            string token = GitHubToken;
            var limit = await Task.Run(() => GitHub.SaveToken(token));
            GitHubToken = "";
            Message = $"GitHub 토큰을 저장했습니다 (시간당 {limit.Limit}회).";
        }
        // 확인 중 네트워크 오류, 저장 중 파일·암호화 오류도 이 화면에서 알린다
        catch (Exception ex) when (Errors.Recoverable(ex) || ex is System.Security.Cryptography.CryptographicException)
        {
            Message = $"토큰을 저장하지 못했습니다: {Errors.Describe(ex)}";
        }
        await RefreshGitHubAsync();
    }

    [RelayCommand]
    private async Task DeleteGitHubToken()
    {
        try
        {
            GitHub.DeleteToken();
            Message = "저장한 GitHub 토큰을 지웠습니다.";
        }
        catch (Exception ex) when (Errors.Recoverable(ex)) { Message = $"토큰을 지우지 못했습니다: {Errors.Describe(ex)}"; }
        await RefreshGitHubAsync();
    }

    [RelayCommand]
    private static void OpenTokenPage() => Shell.Open(GitHub.TokenPageUrl);

    [RelayCommand]
    private void EditGames() => Navigator.Show(new SetupViewModel(firstRun: false));

    [RelayCommand]
    private void BrowseWorkspace()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "작업 폴더 (input / output / 캐시를 두는 곳 — 저장소 폴더도 가능)" };
        if (d.ShowDialog() == true) Workspace = d.FolderName;
    }

    // 없어진 작업 폴더 설정을 지우고 기본 폴더를 쓴다 (작업 폴더 입력칸이 없는 간편 모드에서도 할 수 있게)
    [RelayCommand]
    private void ResetWorkspace()
    {
        try
        {
            AppState.Settings.SaveChanged(s => s.Workspace = null);
            Navigator.Show(new SettingsViewModel($"기본 작업 폴더로 되돌렸습니다: {AppState.Workspace.Root}"));
        }
        catch (Exception ex) when (Errors.IsToolOrFileError(ex))
        {
            Message = $"저장하지 못했습니다: {Errors.Describe(ex)}";
        }
    }

    [RelayCommand]
    private void BrowseIda()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "IDA 설치 폴더 (idat.exe 가 있는 곳)" };
        d.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (d.ShowDialog() == true) Ida = d.FolderName;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            string? workspace = null;
            if (IsAdvanced && !string.IsNullOrWhiteSpace(Workspace))
            {
                workspace = Path.GetFullPath(Workspace.Trim());
                if (CheckWorkspace(workspace) is { } problem)
                {
                    Message = problem;
                    return;
                }
                Directory.CreateDirectory(workspace); // 없는 폴더면 만든다 (없으면 기본 폴더로 넘어가므로)
            }
            AppState.Settings.SaveChanged(s =>
            {
                if (!IsAdvanced) return;
                s.Workspace = workspace;
                s.Hotfix = Hotfix;
            });
            string root = AppState.Workspace.Root;

            // 작업 폴더를 바꿨으면 이 화면의 도구 설정은 옛 작업 폴더의 config.json 값이다 -> 새 폴더에 쓰지 않고
            // 새 작업 폴더의 설정으로 이 화면을 다시 연다
            if (!SamePath(root, _rootLoaded))
            {
                Navigator.Show(new SettingsViewModel(
                    $"작업 폴더를 바꿨습니다: {root}\n아래 도구 설정은 새 작업 폴더의 것입니다. 바꾸려면 고친 뒤 다시 저장하세요."));
                return;
            }
            if (IsAdvanced)
            {
                if (_configError != null)
                {
                    Message = $"화면 설정은 저장했지만, 설정 파일({Config.FileName})을 읽지 못해 도구 설정은 저장하지 않았습니다. 파일을 고친 뒤 다시 여세요.\n({_configError})";
                    return;
                }
                // 한 번에 쓴다 (도중에 실패해도 일부만 바뀌지 않게)
                var changes = new List<KeyValuePair<string, JsonNode?>>();
                if (Oxidizer != _oxidizerLoaded) changes.Add(new("oxidizer", Oxidizer ? JsonValue.Create(true) : null));
                changes.Add(new("sharpVision", Text(SharpVision)));
                if (Ida.Trim() != _idaLoaded) changes.Add(new("ida", Text(Ida))); // 비우면 자동 탐색 ("ida": false 는 그대로 둠)
                changes.Add(new("localDiff", LocalDiff ? null : JsonValue.Create(false))); // 기본 켜짐
                changes.Add(new("python", Text(Python)));
                // 값이 틀려 쓰지 않던 항목은 이 화면의 값(위)으로 바꾸거나 지운다 (idaTarget 처럼 화면에 없는 것도)
                foreach (string key in _settingErrors.Where(k => !changes.Any(x => x.Key == k))) changes.Add(new(key, null));
                Config.Set(root, changes);
                _ = RefreshPythonAsync(string.IsNullOrWhiteSpace(Python) ? null : Python.Trim()); // 고친 경로로 다시 확인
            }
            Message = "저장했습니다.";
        }
        // ArgumentException / NotSupportedException: 작업 폴더 경로의 형식이 틀림 (Path.GetFullPath)
        catch (Exception ex) when (Errors.IsToolOrFileError(ex) || ex is ArgumentException or NotSupportedException)
        {
            Message = $"저장하지 못했습니다: {Errors.Describe(ex)}";
        }
    }

    // 작업 폴더로 쓸 수 있는지 (문제가 있으면 그 설명). 만들기 단계가 작업 폴더의 data\*.txt, output\*.dll 을 지우므로
    // 아무 폴더나(D:\ 등) 받지 않는다: 없는 폴더, 빈 폴더, 이미 작업 폴더인 곳(config.json / input / build.ps1 이 있음)만.
    private static string? CheckWorkspace(string dir)
    {
        if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any()) return null;
        if (File.Exists(Path.Combine(dir, Config.FileName)) || Directory.Exists(Path.Combine(dir, "input")) ||
            File.Exists(Path.Combine(dir, global::Workspace.Marker)))
            return null;
        return $"작업 폴더로 쓸 수 없습니다: {dir}\n이 프로그램이 안의 파일을 지우거나 덮어쓸 수 있으므로, 비어 있는 폴더나 새 폴더 이름, " +
               "또는 이미 쓰던 작업 폴더(config.json 이나 input 폴더가 있는 곳)를 고르세요. 비워 두면 기본 폴더를 씁니다.";
    }

    private static bool SamePath(string a, string b) => Files.SamePath(a, b);

    private static JsonNode? Text(string s) => string.IsNullOrWhiteSpace(s) ? null : JsonValue.Create(s.Trim());

    [RelayCommand]
    private void Back() => Navigator.ShowStart();
}
