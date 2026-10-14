namespace ActPluginRepack.Tests;

// UpdateSignature.PublicKey 는 프로세스 전역이고 시험이 잠깐 바꾸므로, 이 묶음은 다른 시험과 동시에 돌리지 않는다
[CollectionDefinition("signature", DisableParallelization = true)]
public sealed class SignatureCollection;

// 자동 업데이트 묶음 서명: 배포하는 사람의 키로 서명한 "이름 + SHA256" 만 통과
[Collection("signature")]
public class UpdateSignatureTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void AcceptsOnlyMatchingNameHashAndKey()
    {
        string original = UpdateSignature.PublicKey;
        try
        {
            var (privatePem, publicKey) = UpdateSignature.Generate();
            UpdateSignature.PublicKey = publicKey;
            string sig = UpdateSignature.Sign(privatePem, "ActPluginRepack-2.0.1-win-x64.zip", Sha);

            Assert.True(UpdateSignature.Verify("ActPluginRepack-2.0.1-win-x64.zip", Sha.ToUpperInvariant(), sig + "\n"));
            Assert.False(UpdateSignature.Verify("ActPluginRepack-2.0.0-win-x64.zip", Sha, sig));            // 다른 버전 묶음에 옮겨 붙임
            Assert.False(UpdateSignature.Verify("ActPluginRepack-2.0.1-win-x64.zip", Sha[..^1] + "0", sig)); // 바뀐 zip
            Assert.False(UpdateSignature.Verify("ActPluginRepack-2.0.1-win-x64.zip", Sha, "not base64!"));

            var (otherPem, _) = UpdateSignature.Generate();                                              // 다른 사람의 키
            Assert.False(UpdateSignature.Verify("ActPluginRepack-2.0.1-win-x64.zip", Sha,
                UpdateSignature.Sign(otherPem, "ActPluginRepack-2.0.1-win-x64.zip", Sha)));
        }
        finally { UpdateSignature.PublicKey = original; }
    }

    [Fact]
    public void ShippedPublicKeyIsUsable() =>
        Assert.False(string.IsNullOrEmpty(UpdateSignature.PublicKey)); // 비어 있으면 모든 업데이트가 거부된다
}
