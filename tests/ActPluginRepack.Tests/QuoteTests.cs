using System.Runtime.InteropServices;

namespace ActPluginRepack.Tests;

// 관리자 재실행 명령줄: Quote 로 이어 붙인 것을 Windows 가 원래 인수로 되돌리는지
public class QuoteTests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static string[] Split(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            return Enumerable.Range(0, argc).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(argv); }
    }

    private static readonly string[] ArgList =
    [
        "plain",
        "with space",
        @"C:\Program Files (x86)\Advanced Combat Tracker\Plugins\FFXIV_ACT_Plugin_KR",
        "한국어 경로\\파이널판타지14",
        @"C:\trailing\",
        @"C:\trailing space\",
        @"C:\two\\",
        "say \"hi\"",
        "\"",
        @"a\""b",
        @"a\\""b c",
        @"\\server\share\",
        "tab\there",
        "",
        " ",
    ];

    public static TheoryData<string> Args => new(ArgList);

    [Theory]
    [MemberData(nameof(Args))]
    public void SingleArgumentRoundTrips(string arg)
    {
        // 첫 토큰은 프로그램 이름으로 다른 규칙을 따르므로 앞에 하나 둔다
        var parsed = Split("prog.exe " + ElevatedRunner.Quote(arg));
        Assert.Equal(["prog.exe", arg], parsed);
    }

    [Fact]
    public void AllArgumentsTogetherRoundTrip()
    {
        string[] all = ArgList;
        var parsed = Split("prog.exe " + string.Join(" ", all.Select(ElevatedRunner.Quote)));
        Assert.Equal(["prog.exe", .. all], parsed);
    }

    [Fact]
    public void PlainArgumentIsNotQuoted() =>
        Assert.Equal(@"C:\ACT\Plugins", ElevatedRunner.Quote(@"C:\ACT\Plugins"));
}
