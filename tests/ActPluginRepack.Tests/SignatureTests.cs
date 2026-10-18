namespace ActPluginRepack.Tests;

// 시그니처 라이브러리(FFXIV.Signatures — 자체 시험은 lib\FFXIV.Signatures 에)의 오류가 이 도구의 ToolException 으로 오는지
public class SigLibTests
{
    [Fact]
    public void LibraryErrorsBecomeToolErrors()
    {
        Assert.Throws<ToolException>(() => SigLib.Parse("4C 8"));
        Assert.Throws<ToolException>(() => SigLib.Pe("not a pe"u8.ToArray(), "test"));
    }
}

public class SignatureInputTests
{
    [Fact]
    public void SpecificVariantOverridesGeneralLine()
    {
        var resolved = SignatureInput.Resolve(
            [new Deucalion.Sig("ChatLog", "11 22 33 44"), new Deucalion.Sig("TraditionalChinese.ChatLog", "AA BB CC DD")],
            ["Global", "TraditionalChinese"]);
        Assert.Equal("11 22 33 44", resolved.Single(r => r.Variant == "Global").Pattern);
        Assert.Equal("AA BB CC DD", resolved.Single(r => r.Variant == "TraditionalChinese").Pattern);
    }

    [Fact]
    public void LoadRejectsDuplicatesAndStarInDeucalion()
    {
        using var tmp = new TempDir();
        tmp.File("deucalion.txt", "Recv|11 22 33 44\nrecv|11 22 33 55\n");
        Assert.Throws<ToolException>(() => SignatureInput.Load(tmp.Path));
        tmp.File("deucalion.txt", "Send|40 53 ** **\n");
        Assert.Throws<ToolException>(() => SignatureInput.Load(tmp.Path));
        tmp.File("memory.txt", "ChatLog|48 8b ** ** 48 8d\n"); // memory 에서는 Machina 표기 허용
        Assert.Single(SignatureInput.Load(tmp.Path, SignatureInput.MemoryFile));
    }

    [Fact]
    public void UpsertReplacesAndPruneRemovesOnlyAutoLines()
    {
        using var tmp = new TempDir();
        tmp.File("deucalion.txt", "Send|40 53 56 48\n");
        SignatureInput.Upsert(tmp.Path, "deucalion.txt", "Recv", "E8 $ { ' } 4C 8B", "test");
        SignatureInput.Upsert(tmp.Path, "deucalion.txt", "Recv", "E8 $ { ' } 4C 8B 47", "test2");
        var sigs = SignatureInput.Load(tmp.Path);
        Assert.Equal("E8 $ { ' } 4C 8B 47", sigs.Single(s => s.Name == "Recv").Pattern);

        var (removed, manual) = SignatureInput.PruneAuto(tmp.Path, "deucalion.txt", _ => true);
        Assert.Equal(["Recv"], removed);
        Assert.Equal(["Send"], manual);
        Assert.Equal("Send", Assert.Single(SignatureInput.Load(tmp.Path)).Name);
    }
}

public class Crc32Tests
{
    [Fact]
    public void MatchesStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute(0, "123456789"u8));

    [Fact]
    public void CanContinue() =>
        Assert.Equal(Crc32.Compute(0, "123456789"u8), Crc32.Compute(Crc32.Compute(0, "1234"u8), "56789"u8));
}
