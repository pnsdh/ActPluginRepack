// 시그니처 종류 (Sigcheck 맨 위 설명). 기록·비교에 쓰이는 값이라 바꾸지 않는다.
internal static class SignatureKinds
{
    public const string Deucalion = "deucalion"; // 패킷 후킹
    public const string Memory = "memory";       // 플러그인 메모리 읽기
    public const string Oodle = "oodle";         // Machina Oodle 함수
}

// sigcheck 표의 시그니처 출처 (Sigcheck 맨 위 설명). 표에 그대로 보이는 값이라 바꾸지 않는다.
internal static class SignatureSources
{
    public const string Deucalion = "deucalion"; // deucalion dll 내장
    public const string Machina = "machina";     // Machina 가 런타임에 보내는 RECV
    public const string Plugin = "plugin";       // 플러그인 내장
    public const string Input = "input";         // input\signatures 의 교체 시그니처
}
