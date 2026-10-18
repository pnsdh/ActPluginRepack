// FFXIV.Signatures(시그니처 라이브러리)를 부르는 곳. 라이브러리의 오류(SignatureException)를 이 도구의 사용자용
// 오류(ToolException)로 바꿔, 나머지 코드가 지금처럼 ToolException 만 다루면 되게 한다.
internal static class SigLib
{
    public static T Run<T>(Func<T> f)
    {
        try { return f(); }
        catch (SignatureException ex) { throw new ToolException(ex.Message); }
    }

    public static SigPattern Parse(string pattern) => Run(() => SigPattern.Parse(pattern));

    public static PeImage Pe(byte[] bytes, string what) => Run(() => PeImage.Parse(bytes, what));

    public static ScanTarget Target(string exe) => Run(() => new ScanTarget(exe));

    public static CodeImage Code(string exe) => Run(() => new CodeImage(exe));

    // ACTREPACK_DEBUG=1 이면 SigRegen 의 위치 찾기 과정을 로그로
    public static Action<string>? RegenDebug =>
        Environment.GetEnvironmentVariable("ACTREPACK_DEBUG") == "1" ? s => Log.Note("  [sigregen] " + s) : null;
}
