namespace ActPluginRepack.Tests;

public class SigPatternTests
{
    private static ScanTarget Target(TempDir tmp, byte[] text) => new(tmp.File("t.exe", FakePe.Build(text)));

    [Fact]
    public void FollowsCallTargetAndWildcards()
    {
        using var tmp = new TempDir();
        // 90 | E8 rel32(0x10) | 4C 8B 47
        byte[] text = [0x90, 0xE8, 0x10, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0x47];
        var m = Assert.Single(SigPattern.Parse("E8 $ { ' } 4C 8B ?").Scan(Target(tmp, text)));
        Assert.Equal(FakePe.TextRva + 1, m.Rva);
        Assert.Equal(FakePe.TextRva + 2 + 4 + 0x10, m.Saves[0]); // rel32 다음 주소 + 0x10
        Assert.Equal(m.Rva + 8, m.End);
    }

    [Fact]
    public void AlternationAndOverlappingMatches()
    {
        using var tmp = new TempDir();
        byte[] text = [0x48, 0x8B, 0x48, 0x8B, 0x4C, 0x8B];
        var matches = SigPattern.Parse("(48 | 4C) 8B").Scan(Target(tmp, text));
        Assert.Equal(3, matches.Count);
        // 0A(줄바꿈 바이트)도 와일드카드에 맞아야 한다 (Singleline)
        Assert.Single(SigPattern.Parse("11 ? 22").Scan(Target(tmp, [0x11, 0x0A, 0x22])));
    }

    [Theory]
    [InlineData("4C 8")]          // 짝 없는 16진수
    [InlineData("(4C 8B")]        // 괄호
    [InlineData("4C | 8B")]       // 괄호 밖 |
    [InlineData("4C [5-3] 8B")]   // 범위 거꾸로
    [InlineData("4C * 8B")]       // * 하나
    [InlineData("")]
    public void RejectsBadSyntax(string text) => Assert.Throws<ToolException>(() => SigPattern.Parse(text));

    [Fact]
    public void MachinaDoubleStarIsOneByte()
    {
        using var tmp = new TempDir();
        Assert.Single(SigPattern.Parse("11 ** 22").Scan(Target(tmp, [0x11, 0x99, 0x22])));
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
