using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

// 간편 모드 첫 화면: 지금 상태를 쉬운 말로 + 버튼 하나
internal sealed partial class HomeViewModel : ObservableObject
{
    public HomeViewModel()
    {
        Reload();
    }

    [ObservableProperty] private ResultKind _kind;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _primaryText = "확인하고 고치기";
    // 주 버튼을 누를 수 없는 상태(ACT 를 찾지 못함 등)에서만 [새로 고침]을 보인다 — 안내대로 고친 뒤 누르도록.
    // 누를 수 있으면 주 버튼이 다시 확인까지 하므로 둘이 헷갈리기만 한다 (창으로 돌아오면 저절로 다시 읽기도 한다).
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowRefresh))] private bool _canFix = true;
    public bool ShowRefresh => !CanFix;
    [ObservableProperty] private bool _canRestore;
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string? _notice; // 버튼을 누르기 전에 막힌 이유 (ACT 실행 중 등)
    [ObservableProperty] private string? _info;   // 다른 화면에서 넘어온 알림 (깨진 설정 파일을 보관함 등)
    private bool _primaryOpensSetup;              // 주 버튼이 게임 위치 설정을 연다 (설정 오류일 때)
    private const string OfficialUpdateText = "공식 최신본으로 업데이트";

    [RelayCommand]
    private void Refresh()
    {
        Info = null;
        Reload();
    }

    // 지금 상태를 다시 읽는다. 다른 화면에서 넘어온 알림(Info)은 남긴다 — 창으로 돌아올 때마다 부른다.
    public void Reload()
    {
        Notice = null;
        _primaryOpensSetup = false;
        // 작업 폴더나 ACT 파일을 읽지 못해도(권한, 잠긴 파일 등) 화면이 오류로 멈추지 않고 그 이유를 보인다
        try { Show(Status.Collect(AppState.Workspace)); }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            CanRestore = false;
            Details = AppState.WorkspaceFallback ?? "";
            Set(ResultKind.Warning, "지금 상태를 읽지 못했습니다",
                $"{(ex is ToolException ? ex.Message : Errors.Describe(ex))}\n" +
                "잠시 후 [새로 고침]을 누르세요. 계속 이러면 이 프로그램을 다시 실행하세요.", "확인하고 고치기", canFix: false);
        }
    }

    private void Show(StatusReport s)
    {
        CanRestore = s.RestorableBackup != null; // 되돌릴 수 있는 백업만 (설치한 뒤로 ACT 의 플러그인이 바뀌었으면 없음)
        var details = s.Clients.Select(c => $"{Region.LabelOf(c.Region)} 클라이언트: {c.Version ?? "버전 모름"}" +
                                            (s.Prepared?.IsOutdated(c) == true ? " (최신 버전이 아니라 지난 확인에서 제외)" : ""))
            .Append(s.Act != null ? $"ACT 의 FFXIV 플러그인: {s.Act.InstalledVersion ?? "?"}{(s.InstalledIsOurs == true ? " (이 프로그램의 패치본)" : "")}" +
                                    (s.Act.Others.Count > 0 ? " - 해루봇의 한국·글로벌 서버용 모두" : "") : "ACT: 찾지 못함")
            .Concat(AppState.WorkspaceFallback is { } fallback ? [fallback] : Array.Empty<string>())
            .ToList();
        Details = string.Join("\n", details);

        if (s.ConfigError != null)
        {
            // 설정 파일이 문법부터 깨졌으면 그 기술적인 내용(JSON 오류)은 아래 정보 칸에만 — 할 일은 게임 위치를 다시 저장하는 것
            if (AppState.ConfigBroken())
            {
                Details = string.Join("\n", details.Append($"설정 파일 오류: {s.ConfigError}"));
                Set(ResultKind.Warning, "설정 파일을 읽지 못했습니다",
                    "게임 위치를 적어 둔 설정 파일이 깨져서 읽을 수 없습니다. [게임 위치 설정]에서 게임을 고르고 저장하면 " +
                    "새로 만듭니다. (깨진 파일은 옆에 따로 보관합니다)", "게임 위치 설정", canFix: true);
            }
            else
                Set(ResultKind.Warning, "게임 위치를 다시 확인하세요",
                    "설정한 게임 위치에 문제가 있습니다. 게임을 옮겼거나 지웠다면 [게임 위치 설정]에서 다시 고르세요.\n" +
                    $"({s.ConfigError})", "게임 위치 설정", canFix: true);
            _primaryOpensSetup = true;
            return;
        }
        if (s.Act == null)
        {
            // ACT 에 FFXIV 플러그인이 등록돼 있지 않은 것만 "설치한 뒤"로 안내하고, 나머지(켜진 것이 여러 개, 파일이 없음,
            // ACT 설정 파일이 깨짐)는 그 이유를 보인다
            string? reason = s.ActError;
            if (reason == null || s.ActProblem == ActProblem.NotRegistered)
                Set(ResultKind.Warning, "ACT 를 찾지 못했습니다",
                    "해루봇이나 ACT 에서 'FFXIV 플러그인'을 한 번 설치한 뒤 [새로 고침]을 누르세요.\n" +
                    "(이 프로그램은 이미 설치된 FFXIV 플러그인을 고쳐 주는 프로그램입니다)", "확인하고 고치기", canFix: false);
            else if (s.ActProblem == ActProblem.MultipleEnabled)
                Set(ResultKind.Warning, "ACT 에 FFXIV 플러그인이 여러 개 켜져 있습니다",
                    "어느 것을 고칠지 정할 수 없습니다. ACT 의 Plugins 탭에서 쓰지 않는 FFXIV 플러그인을 끄거나 지운 뒤 [새로 고침]을 누르세요.\n" +
                    Friendly.EnabledPlugins(s.ActPaths), "확인하고 고치기", canFix: false);
            else if (s.ActProblem == ActProblem.ChineseOnly)
                Set(ResultKind.Warning, "중국 서버용 FFXIV 플러그인은 고칠 수 없습니다",
                    "해루봇의 중국 서버용 FFXIV 플러그인은 공식 플러그인을 따로 고친 것이라 이 프로그램이 다루지 않습니다.\n" +
                    "한국이나 글로벌 서버에서 쓰려면 해루봇에서 그 서버용 FFXIV 플러그인을 설치한 뒤 [새로 고침]을 누르세요.",
                    "확인하고 고치기", canFix: false);
            else if (s.ActProblem == ActProblem.ConfigUnreadable) // 원문(XML 오류)은 고급 모드의 상태에만
                Set(ResultKind.Warning, "ACT 설정 파일을 읽지 못했습니다",
                    "ACT 를 한 번 실행했다가 끈 뒤 [새로 고침]을 누르세요. 그래도 안 되면 해루봇이나 ACT 에서 FFXIV 플러그인을 다시 설치하세요.",
                    "확인하고 고치기", canFix: false);
            else
                Set(ResultKind.Warning, "ACT 의 FFXIV 플러그인을 확인하세요",
                    $"{reason}\n해루봇이나 ACT 에서 FFXIV 플러그인을 다시 설치한 뒤 [새로 고침]을 누르세요.", "확인하고 고치기", canFix: false);
            return;
        }
        var p = s.Prepared;
        bool fresh = p != null && s.StaleReasons.Count == 0;
        // 패치가 적용됨: ACT 의 것이 output\ 의 패치본과 같고, 그 패치본이 마지막 확인 뒤에 같은 공식 플러그인으로 만든 것
        // (게임이 또 업데이트돼 다시 확인했는데 실패했으면, ACT 에 있는 것은 이전 게임 버전용 패치본이다)
        bool builtFresh = s.Built != null && p != null && s.Built.BuiltAt >= p.PreparedAt && s.Built.PluginVersion == p.PluginVersion;
        if (s.InstalledIsOurs == true && fresh && builtFresh && p!.PatchNeeded == true)
            Set(ResultKind.Success, "패치가 적용되어 있습니다",
                "ACT 를 실행하면 됩니다. 게임이 또 업데이트되면 [다시 확인하기]를 누르세요.", "다시 확인하기", true);
        else if (fresh && p!.PatchNeeded == false && s.OfficialUpdate)
            Set(ResultKind.Info, "공식 최신본으로 바꿀 수 있습니다",
                $"공식 플러그인 {s.BasePluginVersion} 버전이 지금 게임 버전과 맞는데, ACT 에는 " +
                $"{(s.InstalledIsOurs == true ? "이 프로그램으로 만든 패치본" : $"{s.Act?.InstalledVersion ?? "다른"} 버전")}이 설치되어 있습니다.\n" +
                "버튼을 누르면 공식 최신본을 ACT 에 설치합니다. 시작하기 전에 ACT 와 게임을 끄세요.",
                OfficialUpdateText, true);
        else if (fresh && p!.PatchNeeded == false)
            Set(ResultKind.Success, "지금은 패치가 필요 없습니다",
                "공식 플러그인이 지금 게임 버전과 맞고, ACT 에도 공식 플러그인이 설치되어 있습니다. " +
                "게임이 또 업데이트되면 [다시 확인하기]를 누르세요.",
                "다시 확인하기", true);
        else
            Set(ResultKind.Info, "게임 업데이트 후 ACT 가 제대로 동작하지 않을 때",
                "버튼을 누르면 고칠 필요가 있는지 확인하고, 필요하면 고친 플러그인을 만들어 ACT 에 설치까지 합니다.\n" +
                "시작하기 전에 ACT 와 게임을 끄세요.", "확인하고 고치기", true);
    }

    private void Set(ResultKind kind, string headline, string description, string primary, bool canFix)
    {
        Kind = kind;
        Headline = headline;
        Description = description;
        PrimaryText = primary;
        CanFix = canFix;
    }

    [RelayCommand]
    private void Fix()
    {
        if (_primaryOpensSetup)
        {
            Navigator.Show(new SetupViewModel(firstRun: false));
            return;
        }
        try { Install.EnsureClosed(); }
        catch (AppsRunningException ex)
        {
            Notice = Friendly.Describe(ex).Message;
            return;
        }
        // 진행 화면 제목은 누른 버튼에 맞게 (공식 최신본으로 바꾸는 상태면 그 이름으로 — 다시 확인해 보니 고쳐야 하면
        // 작업이 제목을 바꾼다)
        Navigator.ShowRun(PrimaryText == OfficialUpdateText ? OfficialUpdateText : Jobs.FixTitle, Jobs.FixSteps(), Jobs.FixAndInstall);
    }

    [RelayCommand]
    private void Restore()
    {
        try { Install.EnsureClosed(); }
        catch (AppsRunningException ex)
        {
            Notice = Friendly.Describe(ex).Message;
            return;
        }
        Navigator.ShowRun("설치 전으로 되돌리기", Jobs.RestoreSteps(), Jobs.Restore);
    }

    [RelayCommand]
    private void EditGames() => Navigator.Show(new SetupViewModel(firstRun: false));
}
