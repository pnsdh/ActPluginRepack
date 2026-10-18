using System.Windows;
using System.Windows.Threading;

namespace ActPluginRepack.Gui.Tests;

// 화면을 만들 STA 스레드 하나와 그 위의 App (실행할 때와 같은 리소스 — App.xaml 의 테마·Styles.xaml·글꼴).
// App 은 프로세스에 하나뿐이고 만든 스레드에 묶이므로 모든 시험이 이 스레드에서 차례로 돈다.
// OnStartup 은 부르지 않는다 (창을 띄우고 업데이트를 정리한다) — 리소스만 읽는다.
internal static class WpfHost
{
    private static readonly Lazy<(Dispatcher Dispatcher, Exception? Error)> Host = new(Start);

    private static (Dispatcher, Exception?) Start()
    {
        Dispatcher? dispatcher = null;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                // pack://application:,,,/ (글꼴, 아이콘)은 시험 exe 를 가리킨다 — 같은 파일을 시험 프로젝트에도 넣어 둔다 (csproj)
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
            }
            catch (Exception ex) { error = ex; }
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF 시험" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return (dispatcher!, error);
    }

    // 화면 스레드에서 돌린다 (App 을 만들지 못했으면 그 예외로 실패)
    public static void Run(Action action)
    {
        var (dispatcher, error) = Host.Value;
        if (error != null) throw new InvalidOperationException("App 리소스를 읽지 못했습니다.", error);
        dispatcher.Invoke(action);
    }
}
