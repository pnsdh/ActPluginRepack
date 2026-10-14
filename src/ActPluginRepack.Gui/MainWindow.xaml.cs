using ActPluginRepack.Gui.ViewModels;

namespace ActPluginRepack.Gui;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Closing += OnClosing;
    }

    // 작업 중에 창을 닫으면 백그라운드 작업이 도중에 끊긴다 (설치 중이면 ACT 의 파일이 반쯤 바뀐 채로) -> 닫지 않고 알린다
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        string? reason = main.CurrentPage is RunViewModel { IsRunning: true } run
            ? run.Cancelling ? "취소하는 중입니다. 지금 단계가 끝나면 닫을 수 있습니다."
            : run.CanCancel ? "작업이 끝난 뒤에 닫으세요. 멈추려면 [취소]를 누르세요." : "작업이 끝난 뒤에 닫으세요. 지금 단계는 도중에 멈출 수 없습니다."
            : main.Update.Updating ? "업데이트가 끝날 때까지 기다리세요." : null;
        if (reason == null) return;
        e.Cancel = true;
        System.Windows.MessageBox.Show(this, reason, "FFXIV 플러그인 임시 패치",
                                       System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
