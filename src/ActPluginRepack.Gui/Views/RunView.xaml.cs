using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using ActPluginRepack.Gui.ViewModels;

namespace ActPluginRepack.Gui.Views;

public partial class RunView
{
    private bool _scrollPending;
    private RunViewModel? _vm;

    public RunView()
    {
        InitializeComponent();
        // WPF 는 연달아 보이는 진행 화면(설치 뒤 [공식 플러그인으로 되돌리기] 등)에 이 화면을 다시 쓴다 ->
        // 이전 모델의 이벤트를 떼고(옛 기록이 새 화면을 스크롤하지 않게) 맨 위부터 보인다
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.Log.CollectionChanged -= OnLogChanged;
                _vm.PropertyChanged -= OnModelChanged;
            }
            _vm = e.NewValue as RunViewModel;
            if (_vm != null)
            {
                _vm.Log.CollectionChanged += OnLogChanged;
                _vm.PropertyChanged += OnModelChanged;
            }
            TopScroll.ScrollToTop();
            FitTop();
        };
        SizeChanged += (_, _) => FitTop();
    }

    // 새 기록이 오면 맨 아래로
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollLogToEnd();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RunViewModel.ShowLog):
                FitTop();
                // 접혀 있던 동안에는 스크롤되지 않았으므로 펼치면 마지막 줄로
                if (_vm?.ShowLog == true) ScrollLogToEnd();
                if (_vm?.ResultKind is not (null or ResultKind.None)) ShowResult();
                break;
            case nameof(RunViewModel.ResultKind):
                ShowResult();
                break;
        }
    }

    // 목록이 그 항목을 반영한 뒤에 (이벤트 안에서 바로 스크롤하면 목록이 아직 새 항목을 모르는 상태라 WPF 가 항목 수
    // 불일치 오류를 낸다). 접혀 있으면 배치가 없어 스크롤되지 않는다 — 펼칠 때 다시 부른다.
    private void ScrollLogToEnd()
    {
        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    // 결과가 나오면 결과 칸이 보이게 (실패하면 기록이 펼쳐져 위쪽이 줄어들어 결과와 [확인]이 아래로 밀린다).
    // 배치가 끝난 뒤에 — 결과 칸이 방금 보이게 됐거나 위쪽 높이가 방금 바뀌었다.
    private void ShowResult() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (ResultCard.IsVisible) ResultCard.BringIntoView();
        });

    // 위쪽(제목 / 단계 / 결과)은 내용만큼 차지하되 화면보다 길면 스크롤. 기록을 펼쳤으면 기록 자리(아래 약 40%)를 남긴다.
    private void FitTop()
    {
        if (ActualHeight <= 0) return;
        TopScroll.MaxHeight = DataContext is RunViewModel { ShowLog: true } ? Math.Max(160, ActualHeight * 0.6) : ActualHeight;
    }
}
