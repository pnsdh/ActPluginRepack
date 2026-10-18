using System.Reflection;

// 어셈블리 버전 읽기와 비교 (플러그인 설치·상태·공식 플러그인 받기, 이 프로그램의 업데이트가 같은 규칙으로)
internal static class Versions
{
    // dll 의 어셈블리 버전. 없거나 읽을 수 없으면(깨짐, 잠김) null
    public static Version? OfAssembly(string dll)
    {
        if (!File.Exists(dll)) return null;
        try { return AssemblyName.GetAssemblyName(dll).Version; }
        catch (Exception ex) when (ex is BadImageFormatException || Errors.IsFileError(ex)) { return null; }
    }

    // 2/3/4 자리가 섞여도 비교되게 빠진 자리는 0 으로
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static bool IsNewer(Version candidate, Version than) => Normalize(candidate) > Normalize(than);

    // dll 이 than 보다 새 버전인지 (어느 쪽이든 읽지 못하면 false)
    public static bool IsNewer(string dll, Version? than) => than != null && OfAssembly(dll) is { } v && IsNewer(v, than);
    public static bool IsNewer(string dll, string thanDll) => IsNewer(dll, OfAssembly(thanDll));
}
