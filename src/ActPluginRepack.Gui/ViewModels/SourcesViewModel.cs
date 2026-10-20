using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

// 출처 화면의 칸 하나 (게임 / 공식 플러그인 / opcode / 시그니처 / CSV / extras)
internal sealed record SourceSection(string Title, IReadOnlyList<SourceReport.Item> Items);

// 출처: 마지막 확인(prepare)이 무엇을 어디서 가져왔는지 — 게임 exe, 지역별 opcode 와 다른 출처와의 대조, 교체 시그니처,
// 게임 데이터. 첫 화면·고급 모드·진행 화면에서 연다. back: [돌아가기] (연 화면으로)
internal sealed partial class SourcesViewModel : ObservableObject
{
    private readonly Action _back;
    private readonly SourceReport? _report;
    private readonly bool _unreadable; // 파일은 있는데 읽지 못함 (깨짐)

    public SourcesViewModel(Action back)
    {
        _back = back;
        var ws = AppState.Workspace;
        _report = SourceReport.Load(ws);
        _unreadable = _report == null && System.IO.File.Exists(SourceReport.PathOf(ws));
        Sections = _report?.Sections().Select(s => new SourceSection(s.Title, s.Items)).ToList() ?? [];
    }

    public bool HasReport => _report != null;
    public string Summary => _report is { } r
        ? $"{r.PreparedAt:yyyy-MM-dd HH:mm} 에 확인한 결과입니다. {r.Verdict}"
        : _unreadable ? $"지난 확인의 출처 기록을 읽지 못했습니다. [{Titles.Fix}](고급 모드는 prepare)를 다시 실행하면 새로 만듭니다."
        : $"아직 확인한 기록이 없습니다. [{Titles.Fix}](고급 모드는 prepare)를 실행하면 여기에 보입니다.";
    public IReadOnlyList<SourceSection> Sections { get; }

    [ObservableProperty] private string _copyText = "복사";
    [ObservableProperty] private string _openText = "파일로 열기";

    // 마지막 확인이 출처 보고를 남겼는지 (버튼을 보일지)
    public static bool Available(Workspace ws) => System.IO.File.Exists(SourceReport.PathOf(ws));

    [RelayCommand]
    private Task Copy() => _report == null ? Task.CompletedTask : RunViewModel.CopyWithFeedback(_report.ToText(), "복사", t => CopyText = t);

    [RelayCommand]
    private async Task OpenFile()
    {
        string path = SourceReport.TextPathOf(AppState.Workspace);
        // 글자판(extras\sources.txt)이 없어졌으면 읽어 둔 보고로 다시 쓴다
        try { if (!System.IO.File.Exists(path) && _report != null) Files.WriteAtomic(path, _report.ToText()); }
        catch (Exception ex) when (Errors.IsFileError(ex)) { Errors.WriteLog(ex, "gui sources file"); }
        if (System.IO.File.Exists(path) && Shell.Open(path)) return;
        OpenText = "열지 못했습니다";
        await Task.Delay(2000);
        OpenText = "파일로 열기";
    }

    [RelayCommand]
    private void Back() => _back();
}
