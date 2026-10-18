namespace FFXIV.Signatures;

public static class SignatureLibrary
{
    // 공개 API 판. 추가만 하는 동안은 그대로이고, 있던 것의 의미가 바뀌면 올린다 (어셈블리 major 와 함께).
    public static int ApiVersion => 1;
}

// 이 라이브러리가 던지는 오류 — 입력(패턴, exe)이 잘못되었을 때. 메시지는 사용자에게 그대로 보여도 되는 문장이다.
public sealed class SignatureException(string message) : Exception(message);
