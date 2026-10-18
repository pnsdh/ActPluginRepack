using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ActPluginRepack.Gui.ViewModels;
using Wpf.Ui.Controls;

namespace ActPluginRepack.Gui.Views;

internal static class Palette
{
    // 다크 모드에서 눈에 편한 색 (WinUI 다크 테마의 상태 색 기준)
    public static readonly SolidColorBrush Green = Freeze(0x6C, 0xCB, 0x5F);
    public static readonly SolidColorBrush Orange = Freeze(0xFC, 0xD3, 0x4D);
    public static readonly SolidColorBrush Red = Freeze(0xFF, 0x99, 0xA4);
    public static readonly SolidColorBrush Blue = Freeze(0x7A, 0xB7, 0xFF);
    public static readonly SolidColorBrush Gray = Freeze(0x9A, 0xA0, 0xA6);

    // 테마 글자색 (본문 / 흐린 글자)
    public static object Theme(string key) => Application.Current.TryFindResource(key) ?? DependencyProperty.UnsetValue;

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

// 단계 상태 -> 아이콘
internal sealed class StepIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StepState.Running => SymbolRegular.ArrowSync24,
        StepState.Done => SymbolRegular.CheckmarkCircle24,
        StepState.Warning => SymbolRegular.Warning24,
        StepState.Failed => SymbolRegular.ErrorCircle24,
        StepState.Skipped => SymbolRegular.SubtractCircle24,
        _ => SymbolRegular.Circle24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// 단계 상태 / 결과 종류 / 기록 종류 -> 색
internal sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StepState.Running or ResultKind.Info => Palette.Blue,
        StepState.Done or ResultKind.Success or LogKind.Done => Palette.Green,
        StepState.Warning or ResultKind.Warning or LogKind.Warn => Palette.Orange,
        StepState.Failed or ResultKind.Error or LogKind.Error => Palette.Red,
        LogKind.Section => Palette.Blue,
        LogKind.Note => Palette.Theme("TextFillColorTertiaryBrush"),
        LogKind.Info => Palette.Theme("TextFillColorSecondaryBrush"),
        _ => Palette.Gray,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// 결과 종류 -> 큰 아이콘
internal sealed class ResultIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ResultKind.Success => SymbolRegular.CheckmarkCircle24,
        ResultKind.Warning => SymbolRegular.Warning24,
        ResultKind.Error => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// null/빈 문자열/false/ResultKind.None -> Collapsed (parameter "invert" 면 반대)
internal sealed class VisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            ResultKind k => k != ResultKind.None,
            _ => true,
        };
        if (parameter is "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// 지역 -> 한국어 이름
internal sealed class RegionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Region.LabelOf(value as Region);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// 한국어 줄바꿈을 어절 단위로 (CSS 의 word-break: keep-all). WPF 는 한글 음절마다 줄을 바꿀 수 있어
// "관리\n자" 처럼 끊기므로, 공백이 아닌 글자 사이에 WORD JOINER(U+2060)를 넣어 공백에서만 끊기게 한다.
internal sealed class KeepAllConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? KeepAll(s) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string KeepAll(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length * 2);
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (i + 1 < s.Length && (IsHangul(s[i]) || IsHangul(s[i + 1])) && !char.IsWhiteSpace(s[i]) && !char.IsWhiteSpace(s[i + 1]))
                sb.Append('\u2060');
        }
        return sb.ToString();
    }

    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3' or >= '\u3131' and <= '\u318E';
}