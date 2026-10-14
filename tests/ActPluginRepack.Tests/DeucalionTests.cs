using System.Text;

namespace ActPluginRepack.Tests;

// deucalion dll 의 시그니처 읽기 / 같은 길이로 덮어쓰기 (합성 PE)
public class DeucalionTests
{
    private const string Recv = "E8 $ { ' } 4C 8B 43 10 41 8B 4B 18";
    private const string Send = "40 53 56 48 83 EC 38 48 8B D9";
    private const string SendLobby = "40 53 48 83 EC 20 44 8B 41 04";
    private const string Normal = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 85 C0";

    // .rdata: 문자열이 구분자 없이 이어 붙어 있다. CreateTarget 후보 뒤에는 오류 문구.
    private static string Rdata(string recv = Recv, string send = Send, string lobby = SendLobby) =>
        "\0\0hook\0" + recv + send + lobby + "\0\0" + Normal + "Internal normal sig error\0tail";

    private static byte[] Rustc(bool withPatterns = true, string recv = Recv, string send = Send, string lobby = SendLobby) =>
        Encoding.Latin1.GetBytes(withPatterns
            ? $"meta \"{recv}\" x \"{send}\" y \"{lobby}\" z \"{Normal}\" \"not a sig\" \"AB CD\""
            : "\x78\x9C compressed metadata");

    private static byte[] Dll(bool withPatterns = true) =>
        FakePeSections.Build((".text", [0xC3]), (".rdata", Encoding.Latin1.GetBytes(Rdata())), (".rustc", Rustc(withPatterns)));

    [Fact]
    public void NamesHooksByOrderAndCreateTargetByLabel()
    {
        var sigs = Deucalion.ReadSignatures(Dll());
        Assert.Equal(["Recv", "Send", "SendLobby", "CreateTarget.normal"], sigs.Select(s => s.Name));
        Assert.Equal([Recv, Send, SendLobby, Normal], sigs.Select(s => s.Pattern));
    }

    [Fact]
    public void SplitsConcatenatedRdataWhenMetadataIsCompressed()
    {
        // 릴리스 빌드: .rustc 에 따옴표 친 문자열이 없다 -> .rdata 의 "...4B 1840 53..." 을 잘라 읽는다
        var sigs = Deucalion.ReadSignatures(Dll(withPatterns: false));
        Assert.Equal(["Recv", "Send", "SendLobby", "CreateTarget.normal"], sigs.Select(s => s.Name));
        Assert.Equal([Recv, Send, SendLobby, Normal], sigs.Select(s => s.Pattern));
    }

    [Fact]
    public void UnknownHookShapeGetsNumberedNames()
    {
        // Recv 가 맨 앞이 아니면(=$ 가 첫 후크에 없으면) 이름을 짐작하지 않는다
        byte[] dll = FakePeSections.Build(
            (".rdata", Encoding.Latin1.GetBytes(Rdata(recv: Send, send: Recv))),
            (".rustc", Rustc(recv: Send, send: Recv)));
        var sigs = Deucalion.ReadSignatures(dll);
        Assert.Equal(["Hook1", "Hook2", "Hook3", "CreateTarget.normal"], sigs.Select(s => s.Name));
    }

    [Fact]
    public void RejectsMissingSectionsOrSignatures()
    {
        byte[] noRustc = FakePeSections.Build((".rdata", Encoding.Latin1.GetBytes(Rdata())));
        Assert.Throws<ToolException>(() => Deucalion.ReadSignatures(noRustc));
        byte[] empty = FakePeSections.Build((".rdata", "nothing here"u8.ToArray()), (".rustc", "\"x\""u8.ToArray()));
        Assert.Throws<ToolException>(() => Deucalion.ReadSignatures(empty));
        Assert.Throws<ToolException>(() => Deucalion.ReadSignatures("not a pe"u8.ToArray()));
    }

    [Fact]
    public void ShorterPatternIsPaddedInBothCopies()
    {
        byte[] dll = Dll();
        const string shorter = "40 53 56 48 83 EC 38";
        var (patched, changed) = Deucalion.PatchAll(dll, [new Deucalion.Sig("send", shorter)]); // 이름은 대소문자 무시
        Assert.NotNull(patched);
        Assert.Equal(["Send"], changed);
        Assert.Equal(dll.Length, patched.Length);
        string padded = shorter.PadRight(Send.Length);
        Assert.Contains(Recv + padded + SendLobby, FakePeSections.Section(patched, ".rdata")); // 옆 문자열은 그대로
        Assert.Contains($"\"{padded}\"", FakePeSections.Section(patched, ".rustc"));
        Assert.DoesNotContain(Send, FakePeSections.Section(patched, ".rustc"));

        var again = Deucalion.ReadSignatures(patched);
        Assert.Equal(shorter, again.Single(s => s.Name == "Send").Pattern);
        Assert.Equal(SendLobby, again.Single(s => s.Name == "SendLobby").Pattern);
    }

    [Fact]
    public void LongerPatternFitsByRemovingSpaces()
    {
        const string longer = "40 53 56 48 83 EC 38 48 8B D9 90"; // 32자 > 29자
        var (patched, _) = Deucalion.PatchAll(Dll(), [new Deucalion.Sig("Send", longer)]);
        string expected = longer.Replace(" ", "").PadRight(Send.Length);
        Assert.Contains(Recv + expected + SendLobby, FakePeSections.Section(patched!, ".rdata"));
        Assert.Contains($"\"{expected}\"", FakePeSections.Section(patched!, ".rustc"));
    }

    [Fact]
    public void TooLongPatternIsRejected()
    {
        string tooLong = string.Join(" ", Enumerable.Repeat("90", 16)); // 공백을 빼도 32자 > 29자
        var ex = Assert.Throws<ToolException>(() => Deucalion.PatchAll(Dll(), [new Deucalion.Sig("Send", tooLong)]));
        Assert.Contains("너무 깁니다", ex.Message);
        Assert.Throws<ToolException>(() => Deucalion.PatchAll(Dll(), [new Deucalion.Sig("Send", "40 5")])); // 문법 오류
    }

    [Fact]
    public void SameSignatureIgnoringSpacesIsNotAChange()
    {
        var (patched, changed) = Deucalion.PatchAll(Dll(), [new Deucalion.Sig("Send", Send.Replace(" ", ""))]);
        Assert.Null(patched);
        Assert.Empty(changed);
    }

    [Fact]
    public void UnknownNameIsRejected() =>
        Assert.Throws<ToolException>(() => Deucalion.PatchAll(Dll(), [new Deucalion.Sig("Lobby", "40 53 48 83")]));

    [Fact]
    public void SeveralReplacementsUseOriginalPositions()
    {
        const string recv = "E8 $ { ' } 4C 8B 47 10 44 8B 4F 1C";
        const string normal = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 85";
        var (patched, changed) = Deucalion.PatchAll(Dll(withPatterns: false),
            [new Deucalion.Sig("Recv", recv), new Deucalion.Sig("CreateTarget.normal", normal)]);
        Assert.Equal(["Recv", "CreateTarget.normal"], changed);
        string rdata = FakePeSections.Section(patched!, ".rdata");
        Assert.Contains(recv + Send + SendLobby, rdata);
        Assert.Contains(normal.PadRight(Normal.Length) + "Internal normal sig error", rdata);
    }

    [Fact]
    public void ReplacesEveryCopyInRdata()
    {
        // 같은 문자열이 .rdata 에 두 번 있으면 둘 다 바꾼다
        string rdata = Rdata() + "\0" + Send + "\0";
        byte[] dll = FakePeSections.Build((".rdata", Encoding.Latin1.GetBytes(rdata)), (".rustc", Rustc()));
        var (patched, _) = Deucalion.PatchAll(dll, [new Deucalion.Sig("Send", "40 53 56 48 83 EC 38")]);
        string after = FakePeSections.Section(patched!, ".rdata");
        Assert.DoesNotContain(Send, after);
        Assert.Equal(2, after.Split("40 53 56 48 83 EC 38      ").Length - 1);
    }
}
