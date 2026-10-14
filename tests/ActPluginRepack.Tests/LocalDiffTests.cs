namespace ActPluginRepack.Tests;

// 로컬 opcodediff 의 Python 고르기와 가상환경 다시 쓰기 (Python 은 실행하지 않는다)
public class LocalDiffTests
{
    [Fact]
    public void PicksHighestInstalledPython312FromLauncherList()
    {
        const string list = """
             -V:3.14t         C:\Py\Python314\python3.14t.exe
             -V:3.13 *        C:\Py\Python313\python.exe
             -V:3.12          C:\Program Files\Python312\python.exe
             -V:3.11          C:\Py\Python311\python.exe
            """;
        Assert.Equal(@"C:\Py\Python313\python.exe", LocalDiff.PickPython312(list, _ => true));       // * (기본) 표시와 상관없이 높은 것
        Assert.Equal(@"C:\Program Files\Python312\python.exe",                                     // 경로에 공백
                     LocalDiff.PickPython312(list, p => !p.Contains("Python313")));
        Assert.Null(LocalDiff.PickPython312(list, p => p.Contains("Python311")));                   // 3.11 은 멈추므로 쓰지 않음
        Assert.Null(LocalDiff.PickPython312("No installed Pythons found!", _ => true));
    }

    // tools\opcodediff-venv 모양: Scripts\python.exe, requirements 표시, pyvenv.cfg 의 home, (설정한 Python 표시)
    private static string Venv(TempDir tmp, string requirements, string? builtFrom)
    {
        string home = tmp.Dir("base");
        tmp.File("base/python.exe", "");
        string venv = tmp.Dir("venv");
        tmp.File("venv/Scripts/python.exe", "");
        tmp.File("venv/pyvenv.cfg", $"home = {home}\nversion = 3.13.16\n");
        tmp.File("venv/requirements-vtable.txt", requirements);
        if (builtFrom != null) tmp.File("venv/base-python.txt", builtFrom);
        return venv;
    }

    [Fact]
    public void VenvIsRebuiltWhenConfiguredPythonChanges()
    {
        using var tmp = new TempDir();
        string configured = Path.GetFullPath(tmp.File("py/python.exe", ""));
        string other = Path.GetFullPath(tmp.File("py2/python.exe", ""));

        string auto = Venv(tmp, "r2pipe", builtFrom: null);                 // 표시 없음 = 자동으로 찾은 Python 으로 만든 것
        Assert.True(LocalDiff.VenvUpToDate(auto, null, "r2pipe"));
        Assert.False(LocalDiff.VenvUpToDate(auto, null, "r2pipe\nsemver"));  // requirements 바뀜
        Assert.False(LocalDiff.VenvUpToDate(auto, configured, "r2pipe"));    // 설정에 Python 을 적음

        File.WriteAllText(Path.Combine(auto, "base-python.txt"), configured);
        Assert.True(LocalDiff.VenvUpToDate(auto, configured, "r2pipe"));
        Assert.False(LocalDiff.VenvUpToDate(auto, other, "r2pipe"));         // 설정의 Python 을 바꿈
        Assert.False(LocalDiff.VenvUpToDate(auto, null, "r2pipe"));          // 설정을 지움 -> 자동으로 다시

        File.Delete(Path.Combine(tmp.Path, "base", "python.exe"));          // 만든 Python 이 없어짐 (휴대용 버전 교체 등)
        Assert.False(LocalDiff.VenvUpToDate(auto, configured, "r2pipe"));
    }

    [Fact]
    public void ConfiguredPythonMustExist()
    {
        using var tmp = new TempDir();
        Assert.True(LocalDiff.HasPython(null));                          // 없으면 찾거나 휴대용을 받는다
        Assert.False(LocalDiff.HasPython(Path.Combine(tmp.Path, "nope", "python.exe")));
        Assert.True(LocalDiff.HasPython(tmp.File("py/python.exe", "")));
        Assert.Null(LocalDiff.InstalledPython(Path.Combine(tmp.Path, "nope", "python.exe")));
    }
}
