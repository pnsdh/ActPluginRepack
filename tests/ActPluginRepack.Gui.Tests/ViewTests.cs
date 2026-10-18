using System.Reflection;
using System.Windows;
using System.Windows.Markup;

namespace ActPluginRepack.Gui.Tests;

// 화면(XAML)을 모델 없이 만들어 본다: XAML 해석, x:Static, StaticResource(템플릿 안의 것은 배치할 때)가 실행할 때 깨지지 않는지.
// 모델(DataContext)은 붙이지 않는다 — 붙이면 실제 작업 폴더와 gui.json 을 읽는다.
public class ViewTests
{
    private static readonly Size WindowSize = new(960, 700); // MainWindow 의 처음 크기

    // GUI 어셈블리의 XAML 화면 전부 (App, MainWindow 는 따로) — 새 화면을 만들면 저절로 들어온다
    public static TheoryData<string> Views()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(App).Assembly.GetTypes()
                     .Where(t => typeof(FrameworkElement).IsAssignableFrom(t) && typeof(IComponentConnector).IsAssignableFrom(t) &&
                                 t != typeof(MainWindow))
                     .OrderBy(t => t.FullName))
            data.Add(type.FullName!);
        return data;
    }

    [Fact]
    public void ListsKnownViews()
    {
        var names = Views().Select(row => row.Data).ToList();
        foreach (string view in new[] { "HomeView", "SettingsView", "SetupView", "HelpView", "AdvancedView", "RunView" })
            Assert.Contains($"ActPluginRepack.Gui.Views.{view}", names);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void ViewLoads(string typeName) => WpfHost.Run(() =>
    {
        var type = typeof(App).Assembly.GetType(typeName, throwOnError: true)!;
        var view = (FrameworkElement)Activator.CreateInstance(type, nonPublic: true)!; // 생성자가 InitializeComponent 를 부른다
        Assert.Null(view.DataContext);
        Layout(view, WindowSize);
    });

    [Fact]
    public void MainWindowLoads() => WpfHost.Run(() =>
    {
        var window = new MainWindow(attachModel: false); // 띄우지 않는다
        try
        {
            Assert.Null(window.DataContext);
            Assert.StartsWith(Titles.App, window.Title);
            Layout((FrameworkElement)window.Content, WindowSize);
        }
        finally { window.Close(); }
    });

    // 실행할 때와 같은 리소스가 있는지 (App.xaml 의 사전을 읽었는지)
    [Fact]
    public void AppResourcesLoaded() => WpfHost.Run(() =>
    {
        foreach (string key in new[] { "KeepAll", "Visible", "Body", "Headline", "AppFont", "MonoFont" })
            Assert.NotNull(Application.Current.TryFindResource(key));
    });

    // 배치까지 (템플릿을 적용한다 — 템플릿 안의 StaticResource 가 이때 풀린다)
    private static void Layout(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        Assert.True(element.ActualWidth > 0);
    }
}
