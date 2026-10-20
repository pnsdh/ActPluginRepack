using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

internal sealed partial class ClientRow : ObservableObject
{
    public ClientRow(string dir, Region? region, string? version, string source, bool selected)
    {
        InstallDir = dir;
        _region = region;
        Version = version;
        Source = source;
        _isSelected = selected;
    }

    public string InstallDir { get; }
    public string? Version { get; }
    public string Source { get; }
    [ObservableProperty] private Region? _region;
    [ObservableProperty] private bool _isSelected;
    public static IReadOnlyList<Region> Regions => Region.All;
}

// 게임 위치 설정 (첫 실행 마법사 겸 설정): 자동으로 찾은 클라이언트를 확인받는다
internal sealed partial class SetupViewModel : ObservableObject
{
    public SetupViewModel(bool firstRun)
    {
        FirstRun = firstRun;
        Clients.CollectionChanged += (_, e) =>
        {
            // 지역·체크를 바꾸면 중국·대만 안내도 다시 (직접 추가한 줄은 다시 찾을 때 또 들어오므로 한 번만 붙인다)
            foreach (ClientRow row in e.NewItems ?? Array.Empty<ClientRow>())
            {
                row.PropertyChanged -= OnRowChanged;
                row.PropertyChanged += OnRowChanged;
            }
            OnPropertyChanged(nameof(Intro));
        };
        Detect();
    }

    public bool FirstRun { get; }
    public ObservableCollection<ClientRow> Clients { get; } = [];
    [ObservableProperty] private string? _actStatus;
    [ObservableProperty] private bool _actFound;
    [ObservableProperty] private string? _error;

    public string Intro => (Clients.Count > 0
        ? "이 컴퓨터에 설치된 파이널판타지14 를 찾았습니다. 쓰는 클라이언트에 체크하고 [저장]을 누르세요. 목록에 없으면 [폴더 직접 고르기]로 추가할 수 있습니다."
        : "이 컴퓨터에서 파이널판타지14 를 자동으로 찾지 못했습니다. [폴더 직접 고르기]로 게임이 설치된 폴더를 고른 뒤 [저장]을 누르세요.") +
        (Clients.Any(c => c.Region != null && !ClientLocator.SelectedByDefault(c.Region) && !c.IsSelected)
            ? "\n중국·대만 서버용 클라이언트는 기본으로 고르지 않습니다. 해루봇의 중국 서버용 플러그인은 이 프로그램이 고치지 않는 다른 플러그인입니다. " +
              "이 프로그램이 고치는 FFXIV 플러그인을 그 서버에서도 확인하려면 체크하세요."
            : "");

    private const string ManualSource = "직접 추가";

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Intro));

    [RelayCommand]
    private void Detect()
    {
        var (config, configError) = AppState.TryLoadConfig();
        // 설정 파일이 깨졌으면 미리 알린다: 문법부터 깨졌으면 저장할 때 옆에 보관하고 새로 만든다
        Error = configError == null ? null
            : AppState.ConfigBroken()
                ? $"설정 파일({Config.FileName})이 깨져서 읽을 수 없습니다. 게임을 고르고 [저장]을 누르면 깨진 파일은 " +
                  $"옆에 따로 보관하고 새로 만듭니다.\n({configError})"
                : configError;
        var current = config.Clients;
        // 직접 추가한 줄은 다시 찾아도 남긴다 (자동으로 찾은 것과 겹치면 자동 쪽만)
        var manual = Clients.Where(r => r.Source == ManualSource).ToList();
        Clients.Clear();
        // 처음 고를 때: 설정된 클라이언트가 있으면 그것만 체크한다 (일부러 뺀 것이 다시 들어가거나, 옛 설치 폴더가 같이 체크돼 같은 지역이
        // 둘이 되어 저장이 막히지 않게). 설정이 비었으면 한국·글로벌을 지역마다 처음 찾은 하나씩
        var firstOfRegion = new HashSet<string>();
        // 설정에 적혀 있지만 경로가 틀린 지역 (게임을 옮겼거나 다시 설치함) — 새 위치에서 찾은 그 지역의 첫 줄을 체크한다
        var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string key in Config.RawClients(AppState.Workspace.Root).Keys)
                if (current.All(c => !string.Equals(c.Region?.Name, key, StringComparison.OrdinalIgnoreCase))) broken.Add(key);
        }
        catch (Exception ex) when (Errors.IsToolOrFileError(ex)) { } // 설정 파일이 깨짐 — 위의 안내로
        foreach (var d in ClientLocator.Detect())
        {
            var written = current.FirstOrDefault(c => SameDir(InstallDirOf(c.ExePath), d.InstallDir))?.Region;
            var region = written != null && (d.Region == null || !ClientLocator.Contradicts(d.Region, written)) ? written : d.Region;
            // 적어 둔 지역과 달라 고친 줄(예전에 global 로 적힌 중국판)은 기본 규칙으로 — 중국·대만판은 체크하지 않고 안내를 보인다
            bool selected = current.Count > 0
                ? written != null ? region == written || ClientLocator.SelectedByDefault(region)
                  : region != null && broken.Contains(region.Name) && firstOfRegion.Add(region.Name)
                : ClientLocator.SelectedByDefault(d.Region) && firstOfRegion.Add(d.Region!.Name);
            Clients.Add(new ClientRow(d.InstallDir, region, d.Version, d.Source, selected));
        }
        // 이미 설정돼 있는데 자동으로 못 찾은 것도 보여 준다
        foreach (var c in current.Where(c => !Listed(InstallDirOf(c.ExePath))))
            Clients.Add(new ClientRow(InstallDirOf(c.ExePath), c.Region, c.Version, "현재 설정", selected: true));
        foreach (var r in manual.Where(r => !Listed(r.InstallDir))) Clients.Add(r);

        try
        {
            var act = ActLocator.ResolveTarget(null);
            ActFound = true;
            ActStatus = $"ACT 의 FFXIV 플러그인을 찾았습니다: {string.Join(", ", act.PluginDirs)}";
        }
        // 등록돼 있지 않은 것만 "먼저 설치"로 안내하고, 나머지(켜진 것이 여러 개, 파일이 없음, ACT 설정 파일이 깨짐)는 그 이유를 보인다
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            ActFound = false;
            var target = ex as ActTargetException;
            var (title, text) = Friendly.ActProblemText(target?.Problem, target?.Message ?? Errors.Describe(ex), target?.Paths);
            ActStatus = $"{title}. {text}\n(나중에 해도 됩니다)"; // text 가 괄호로 끝나기도 해서 줄을 나눈다
        }
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "파이널판타지14 가 설치된 폴더를 고르세요 (boot, game 폴더가 있는 곳)" };
        if (dialog.ShowDialog() != true) return;
        string dir = dialog.FolderName;
        if (Path.GetFileName(dir).Equals("game", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
        if (!File.Exists(Path.Combine(dir, "game", "ffxiv_dx11.exe")))
        {
            Error = "그 폴더에서 게임 클라이언트를 찾지 못했습니다. 'boot' 와 'game' 폴더가 들어 있는 폴더를 고르세요.";
            return;
        }
        // 이미 목록에 있는 폴더면 새 줄을 만들지 않고 그 줄을 고른다
        if (Clients.FirstOrDefault(r => SameDir(r.InstallDir, dir)) is { } existing)
        {
            existing.IsSelected = true;
            Error = null;
            return;
        }
        Error = null;
        Clients.Add(new ClientRow(dir, ClientLocator.InferRegion(dir), GameVersion.ReadFrom(Path.Combine(dir, "game")),
                                  ManualSource, selected: true));
    }

    [RelayCommand]
    private void Save()
    {
        var chosen = Clients.Where(c => c.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            Error = "클라이언트를 하나 이상 고르세요.";
            return;
        }
        if (chosen.FirstOrDefault(c => c.Region == null) is { } unknown)
        {
            Error = $"이 클라이언트가 어느 지역(한국/글로벌 등) 것인지 고르세요: {unknown.InstallDir}";
            return;
        }
        if (chosen.GroupBy(c => c.Region!.Name).FirstOrDefault(g => g.Count() > 1) is { } dup)
        {
            Error = $"같은 지역({dup.First().Region!.Label}) 클라이언트는 하나만 고를 수 있습니다.";
            return;
        }
        string? kept;
        try
        {
            // 문법부터 깨진 설정 파일은 고쳐 쓸 수 없다 -> 옆에 보관하고(.broken-시각) 새로 만든다
            string root = AppState.Workspace.Root;
            kept = Config.MoveBroken(root);
            Config.SetClients(root, chosen.ToDictionary(c => c.Region!.Name, c => c.InstallDir));
        }
        catch (Exception ex) when (Errors.IsToolOrFileError(ex))
        {
            Error = $"저장하지 못했습니다: {Errors.Describe(ex)}\n" +
                    $"(설정 파일: {AppState.Workspace.ConfigPath})";
            return;
        }
        Navigator.ShowStart();
        // 보관한 파일은 시작 화면에 알린다 (고급 설정 등을 거기서 다시 옮겨 적을 수 있게)
        if (kept != null) Navigator.Notify($"읽을 수 없던 설정 파일은 따로 보관하고 새로 만들었습니다: {kept}");
    }

    [RelayCommand]
    private void Cancel() => Navigator.ShowStart();

    private bool Listed(string dir) => Clients.Any(r => SameDir(r.InstallDir, dir));
    private static string InstallDirOf(string exe) => Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!;
    private static bool SameDir(string a, string b) => Files.SamePath(a, b);
}
