using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

// 첫 화면 주 버튼이 하는 일
internal enum PrimaryAction
{
    Fix,            // 확인하고(필요하면) 고쳐 설치 — [확인하고 고치기] / [다시 확인하기]
    OfficialUpdate, // 같은 작업이지만 공식 최신본으로 바꿀 상태 — 진행 화면 제목이 그 이름
    OpenSetup,      // 게임 위치 설정을 연다 (설정 오류일 때)
}

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
    [ObservableProperty] private string _primaryText = Titles.Fix;
    private PrimaryAction _primary = PrimaryAction.Fix;
    // 주 버튼을 누를 수 없는 상태(ACT 를 찾지 못함 등)에서만 [새로 고침]을 보인다 — 안내대로 고친 뒤 누르도록.
    // 누를 수 있으면 주 버튼이 다시 확인까지 하므로 둘이 헷갈리기만 한다 (창으로 돌아오면 저절로 다시 읽기도 한다).
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowRefresh))] private bool _canFix = true;
    public bool ShowRefresh => !CanFix;
    [ObservableProperty] private bool _canRestore;
    [ObservableProperty] private bool _hasSources; // 지난 확인의 출처 보고가 있으면 [출처]
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string? _notice; // 버튼을 누르기 전에 막힌 이유 (ACT 실행 중 등)
    [ObservableProperty] private string? _info;   // 다른 화면에서 넘어온 알림 (깨진 설정 파일을 보관함 등)

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
        // 작업 폴더나 ACT 파일을 읽지 못해도(권한, 잠긴 파일 등) 화면이 오류로 멈추지 않고 그 이유를 보인다
        try
        {
            HasSources = SourcesViewModel.Available(AppState.Workspace);
            Show(Status.Collect(AppState.Workspace));
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            CanRestore = false;
            Details = AppState.WorkspaceFallback ?? "";
            Set(ResultKind.Warning, "지금 상태를 읽지 못했습니다",
                $"{Errors.Describe(ex)}\n" +
                "잠시 후 [새로 고침]을 누르세요. 계속 이러면 이 프로그램을 다시 실행하세요.", PrimaryAction.Fix, canFix: false);
        }
    }

    // 어떤 안내를 보일지는 Core 가 정한다 (HomeScreen.Decide) — 여기서는 문구와 버튼으로
    private void Show(StatusReport s)
    {
        var d = HomeScreen.Decide(s, AppState.ConfigBroken);
        CanRestore = d.CanRestore;
        var details = s.Clients.Select(c => $"{Region.LabelOf(c.Region)} 클라이언트: {c.Version ?? "버전 모름"}" +
                                            (s.Prepared?.IsOutdated(c) == true ? " (최신 버전이 아니라 지난 확인에서 제외)" : ""))
            .Append(s.Act != null ? $"ACT 의 FFXIV 플러그인: {ActVersions(s.Act)}{(s.PatchInstalled ? " (이 프로그램이 고친 플러그인)" : "")}" : "ACT: 찾지 못함")
            .Concat(AppState.WorkspaceFallback is { } fallback ? [fallback] : Array.Empty<string>())
            .Concat((s.SettingErrors ?? []).Select(e => $"설정 파일의 이 항목은 값이 틀려 쓰지 않습니다: {e}"))
            .ToList();
        Details = string.Join("\n", details);

        switch (d.Status)
        {
            // 설정 파일이 문법부터 깨졌으면 그 기술적인 내용(JSON 오류)은 아래 정보 칸에만 — 할 일은 게임 위치를 다시 저장하는 것
            case HomeStatus.ConfigBroken:
                Details = string.Join("\n", details.Append($"설정 파일 오류: {s.ConfigError}"));
                Set(ResultKind.Warning, "설정 파일을 읽지 못했습니다",
                    $"게임 위치를 적어 둔 설정 파일이 깨져서 읽을 수 없습니다. [{Titles.GameLocation}]에서 게임을 고르고 저장하면 " +
                    "새로 만듭니다. (깨진 파일은 옆에 따로 보관합니다)", PrimaryAction.OpenSetup, canFix: true);
                break;
            case HomeStatus.ConfigProblem:
                Set(ResultKind.Warning, "게임 위치를 다시 확인하세요",
                    $"설정한 게임 위치에 문제가 있습니다. 게임을 옮겼거나 지웠다면 [{Titles.GameLocation}]에서 다시 고르세요.\n" +
                    $"({s.ConfigError})", PrimaryAction.OpenSetup, canFix: true);
                break;
            case HomeStatus.ActMissing:
                var (title, text) = Friendly.ActProblemText(d.ActProblem, s.ActError, s.ActPaths);
                Set(ResultKind.Warning, title, $"{text}\n그 뒤 [새로 고침]을 누르세요.", PrimaryAction.Fix, canFix: false);
                break;
            case HomeStatus.Patched:
                Set(ResultKind.Success, "패치가 적용되어 있습니다",
                    d.Unconfirmed
                        ? $"{Notes.LaunchAct}\n다만 {Notes.Unconfirmed}"
                        : $"{Notes.LaunchAct}\n{Notes.NextUpdate}", PrimaryAction.Fix, true, Titles.Recheck);
                break;
            case HomeStatus.OfficialUpdate:
                Set(ResultKind.Info, "공식 최신본으로 바꿀 수 있습니다",
                    $"공식 플러그인 {s.BasePluginVersion} 버전이 지금 게임 버전과 맞는데, ACT 에는 " +
                    $"{(s.PatchInstalled ? "이 프로그램이 고친 플러그인" : $"{s.Act?.InstalledVersion ?? "다른"} 버전")}이 설치되어 있습니다.\n" +
                    $"버튼을 누르면 공식 최신본을 ACT 에 설치합니다. {Notes.CloseAppsFirst}",
                    PrimaryAction.OfficialUpdate, true);
                break;
            case HomeStatus.NewerInstalled:
                Set(ResultKind.Info, "ACT 에 더 새 플러그인이 있습니다",
                    Notes.NewerPlugin(s.Act?.InstalledVersion ?? "?", s.BasePluginVersion, $"[{Titles.Recheck}]를 누르세요."),
                    PrimaryAction.Fix, true, Titles.Recheck);
                break;
            case HomeStatus.NotNeeded:
                Set(ResultKind.Success, Notes.NotNeededTitle, Notes.NotNeeded, PrimaryAction.Fix, true, Titles.Recheck);
                break;
            default:
                Set(ResultKind.Info, "게임 업데이트 후 ACT 가 제대로 동작하지 않을 때",
                    "버튼을 누르면 고칠 필요가 있는지 확인하고, 필요하면 고친 플러그인을 만들어 ACT 에 설치까지 합니다.\n" +
                    Notes.CloseAppsFirst, PrimaryAction.Fix, true);
                break;
        }
    }

    // 설치된 버전. 해루봇의 언어별 폴더가 여럿이면 같을 때만 하나로, 다르면 폴더마다 ("한국 3.0.3.1 / 글로벌 3.0.3.3")
    private static string ActVersions(ActTarget act)
    {
        if (act.Others.Count == 0) return act.InstalledVersion ?? "?";
        var each = act.PluginPaths.Select(f => (Folder: FolderLabel(System.IO.Path.GetDirectoryName(f)!), Version: MaterialState.PluginVersionOf(f) ?? "?")).ToList();
        return each.Select(e => e.Version).Distinct().Count() == 1
            ? $"{each[0].Version} - 해루봇의 {string.Join("·", each.Select(e => e.Folder))} 서버용 모두"
            : string.Join(" / ", each.Select(e => $"{e.Folder} {e.Version}"));
    }

    private static string FolderLabel(string dir) => ActLocator.HaerubotLabel(dir) ?? System.IO.Path.GetFileName(dir);

    // text: 주 버튼 글자 (없으면 그 동작의 이름 — Fix 를 [다시 확인하기]로 보일 때만 준다)
    private void Set(ResultKind kind, string headline, string description, PrimaryAction primary, bool canFix, string? text = null)
    {
        Kind = kind;
        Headline = headline;
        Description = description;
        _primary = primary;
        PrimaryText = text ?? primary switch
        {
            PrimaryAction.OfficialUpdate => Titles.OfficialUpdate,
            PrimaryAction.OpenSetup => Titles.GameLocation,
            _ => Titles.Fix,
        };
        CanFix = canFix;
    }

    [RelayCommand]
    private void Fix()
    {
        if (_primary == PrimaryAction.OpenSetup)
        {
            Navigator.Show(new SetupViewModel(firstRun: false));
            return;
        }
        // 진행 화면 제목은 누른 버튼에 맞게 (공식 최신본으로 바꾸는 상태면 그 이름으로 — 다시 확인해 보니 고쳐야 하면
        // 작업이 제목을 바꾼다)
        Navigator.TryShowRun(_primary == PrimaryAction.OfficialUpdate ? Titles.OfficialUpdate : Titles.Fix,
                             Jobs.FixSteps(), Jobs.FixAndInstall, n => Notice = n);
    }

    [RelayCommand]
    private void Restore() => Navigator.TryShowRun(Titles.Restore, Jobs.RestoreSteps(), Jobs.Restore, n => Notice = n);

    [RelayCommand]
    private void EditGames() => Navigator.Show(new SetupViewModel(firstRun: false));

    [RelayCommand]
    private void OpenHelp() => Navigator.Show(new HelpViewModel());

    [RelayCommand]
    private static void OpenSources() => Navigator.Show(new SourcesViewModel(Navigator.ShowStart));
}
