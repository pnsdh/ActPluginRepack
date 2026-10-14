using System.Security.Cryptography;
using System.Text;

// 자동 업데이트 묶음의 서명 (ECDSA P-256, SHA-256).
//
// 같은 릴리스에 올린 .sha256 은 받다가 깨진 것만 막는다 — GitHub 계정이나 토큰이 털리면 zip 과 .sha256 을 같이 바꿀 수
// 있고, 묶음 안의 cli\ActPluginRepack.exe 는 나중에 관리자 권한으로 실행된다. 그래서 배포하는 사람의 PC 에만 있는 개인키로
// "묶음 이름 + SHA256" 에 서명하고(publish.ps1 -> CLI release-sign), 앱은 아래 공개키로 확인한다. 서명이 없거나 맞지 않으면
// 업데이트하지 않는다 (직접 받기는 그대로 가능).
//
// 개인키: %USERPROFILE%\.actrepack\release-signing-key.pem (환경 변수 ACTREPACK_SIGNING_KEY 로 다른 위치). 저장소에 넣지 않는다.
// 키를 잃으면 새 키를 만들고 공개키를 바꾼 버전을 직접 받기로 한 번 배포해야 한다 — 키 파일은 따로 백업해 둘 것.
internal static class UpdateSignature
{
    // 공개키 (SubjectPublicKeyInfo, base64). 시험에서만 바꾼다.
    internal static string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEf8niGyfcxGrM7uZEWO0BZl9yYf6llx1Ec4aIe1bKTcELsfzIFNM/25XQq5SquVAgD9Jyp7QbUV7W6LEgP5S/0A==";

    public static string DefaultKeyPath => Environment.GetEnvironmentVariable("ACTREPACK_SIGNING_KEY") is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".actrepack", "release-signing-key.pem");

    // 서명하는 내용: 용도 + 묶음 파일 이름 + SHA256 (다른 버전의 묶음에 서명을 옮겨 붙이지 못하게 이름도 넣는다)
    private static byte[] Message(string assetName, string sha256) =>
        Encoding.UTF8.GetBytes($"ActPluginRepack update v1\n{assetName}\n{sha256.Trim().ToLowerInvariant()}\n");

    public static bool Verify(string assetName, string sha256, string signature)
    {
        if (PublicKey.Length == 0) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            return key.VerifyData(Message(assetName, sha256), Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public static string Sign(string privateKeyPem, string assetName, string sha256)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.SignData(Message(assetName, sha256), HashAlgorithmName.SHA256));
    }

    // 새 키: (개인키 PEM, 공개키 base64)
    public static (string PrivatePem, string PublicKey) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
}
