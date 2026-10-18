namespace FFXIV.Signatures.Tests;

public class SigPatternTests
{
    private static ScanTarget Target(byte[] text) => FakePe.Target(FakePe.Text(text));

    [Fact]
    public void FollowsCallTargetAndWildcards()
    {
        // 90 | E8 rel32(0x10) | 4C 8B 47
        byte[] text = [0x90, 0xE8, 0x10, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0x47];
        var m = Assert.Single(SigPattern.Parse("E8 $ { ' } 4C 8B ?").Scan(Target(text)));
        Assert.Equal(FakePe.TextRva + 1, m.Rva);
        Assert.Equal(FakePe.TextRva + 2 + 4 + 0x10, m.Saves[0]); // rel32 다음 주소 + 0x10
        Assert.Equal(m.Rva + 8, m.End);
        Assert.Equal(".text", m.Section);
    }

    [Fact]
    public void AlternationAndOverlappingMatches()
    {
        byte[] text = [0x48, 0x8B, 0x48, 0x8B, 0x4C, 0x8B];
        var matches = SigPattern.Parse("(48 | 4C) 8B").Scan(Target(text));
        Assert.Equal(3, matches.Count);
        // 0A(줄바꿈 바이트)도 와일드카드에 맞아야 한다 (Singleline)
        Assert.Single(SigPattern.Parse("11 ? 22").Scan(Target([0x11, 0x0A, 0x22])));
    }

    [Theory]
    [InlineData("4C 8")]          // 짝 없는 16진수
    [InlineData("(4C 8B")]        // 괄호
    [InlineData("4C | 8B")]       // 괄호 밖 |
    [InlineData("4C [5-3] 8B")]   // 범위 거꾸로
    [InlineData("4C * 8B")]       // * 하나
    [InlineData("")]
    public void RejectsBadSyntax(string text) => Assert.Throws<SignatureException>(() => SigPattern.Parse(text));

    [Fact]
    public void MachinaDoubleStarIsOneByte() =>
        Assert.Single(SigPattern.Parse("11 ** 22").Scan(Target([0x11, 0x99, 0x22])));

    // OverlayPlugin / cactbot 표기: 두 글자 = 1바이트, "??" = 와일드카드 1바이트, 대소문자 섞임
    [Fact]
    public void HexSyntaxIsTwoCharactersPerByte()
    {
        Assert.Equal("48 8B ? 0F 84 ? ? ? ? 3B", SigPattern.HexToPelite("488b??0f84????????3B"));
        byte[] text = [0x74, 0x05, 0x80, 0x3D, 1, 2, 3, 4, 5, 0x74, 0x09];
        var m = Assert.Single(SigPattern.Parse("74??803D??????????74??", SigSyntax.Hex).Scan(Target(text)));
        Assert.Equal(11, m.Length);
    }

    [Theory]
    [InlineData("488")]    // 홀수
    [InlineData("48?8")]   // 반쪽 와일드카드
    [InlineData("48GG")]
    [InlineData("")]
    public void RejectsBadHex(string hex) => Assert.Throws<SignatureException>(() => SigPattern.Parse(hex, SigSyntax.Hex));

    // 고정 바이트가 없으면 모든 위치에 맞는다 / 건너뛰기에 공백·부호를 넣으면 정규식이 글자로 읽는다
    [Theory]
    [InlineData("? ? ? ?")]
    [InlineData("[4]")]
    [InlineData("?? [2-4]")]
    [InlineData("48 [ 2 ] 8B")]
    [InlineData("48 [+3] 8B")]
    [InlineData("48 [3 -5] 8B")]
    [InlineData("(48 | ?) ? ?")]   // 고정 바이트가 대안의 한쪽에만
    [InlineData("(4C | ) ?")]
    public void RejectsPatternsThatWouldMatchWrongly(string text) => Assert.Throws<SignatureException>(() => SigPattern.Parse(text));

    // 건너뛰기를 겹친 패턴은 되짚기가 폭증한다 — 시간 초과 예외가 새어 나가지 않고 형식 오류로 거부
    [Fact]
    public void HugeNestedSkipsAreRejectedQuickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<SignatureException>(() => SigPattern.Parse("? [0-16384] ? [0-16384] 48"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    [Fact]
    public void HexWildcardsOnlyIsRejected() => Assert.Throws<SignatureException>(() => SigPattern.Parse("????????", SigSyntax.Hex));

    [Fact]
    public void PeliteToHexForSigRegenOutput()
    {
        Assert.Equal("E8????????4C8B??", SigPattern.PeliteToHex("E8 $ { ' } 4C 8b ?"));
        Assert.Equal("488B??", SigPattern.PeliteToHex("48 8B **"));
        Assert.Throws<SignatureException>(() => SigPattern.PeliteToHex("(48 | 4C) 8B"));
        Assert.Throws<SignatureException>(() => SigPattern.PeliteToHex("48 [2] 8B"));
    }

    [Fact]
    public void AllSectionsScansDataToo()
    {
        byte[] marker = [0xDE, 0xAD, 0xBE, 0xEF];
        var pe = FakePe.Build((".text", [0x90, .. marker], FakePe.Code, 0), (".rdata", [0, 0, .. marker], FakePe.Data, 0));
        var sig = SigPattern.Parse("DE AD BE EF");
        Assert.Single(sig.Scan(FakePe.Target(pe)));
        var all = sig.Scan(FakePe.Target(pe, ScanScope.AllSections));
        Assert.Equal([(".text", 0x1001), (".rdata", 0x2002)], all.Select(m => (m.Section, m.Rva)));
    }

    // 64KB 덩어리로 읽는 런타임 스캐너가 놓치는 일치
    [Fact]
    public void CrossesBoundary()
    {
        Assert.False(new SigPattern.Match(0xFFF0, [], 0x10).CrossesBoundary());
        Assert.True(new SigPattern.Match(0xFFF1, [], 0x10).CrossesBoundary());
        Assert.False(new SigPattern.Match(0x10000, [], 0x10).CrossesBoundary());
    }
}

public class PeImageTests
{
    [Fact]
    public void ReadsImageBaseSectionsAndFileOffsets()
    {
        var pe = FakePe.Image(FakePe.Build((".text", [1, 2, 3], FakePe.Code, 0), (".data", [4, 5, 6, 7], FakePe.Data, 0x100)));
        Assert.Equal(FakePe.ImageBase, pe.ImageBase);
        Assert.True(pe.Require(".text").Executable);
        Assert.False(pe.Require(".data").Executable);
        Assert.Equal(".data", pe.SectionAt(0x2000 + 0x280)?.Name); // 가상 꼬리(bss)도 .data
        Assert.Null(pe.FileOffset(0x2000 + 0x280));               // 하지만 파일에는 없다
        Assert.Equal(0x400, pe.FileOffset(0x2000));
        Assert.Null(pe.SectionAt(0x5000));
        Assert.Equal(0x07060504, pe.ReadInt32(0x2000));
        Assert.Null(pe.ReadInt32(0x2000 + 0x1FE)); // 끝이 파일 밖 (가상 꼬리)
    }

    // 헤더는 섹션이 더 길다고 하는데 파일이 잘림: 파일에 없는 자리는 없는 것으로
    [Fact]
    public void TruncatedFileHasNoOffsetPastTheEnd()
    {
        byte[] full = FakePe.Build((".text", new byte[0x300], FakePe.Code, 0));
        var pe = FakePe.Image(full[..0x300]); // .text 는 0x200 부터 0x400 까지라고 적혀 있음
        Assert.Equal(0x200, pe.FileOffset(0x1000));
        Assert.Null(pe.FileOffset(0x1000 + 0x100));
        Assert.Null(pe.ReadInt32(0x1000 + 0x0FE));
    }

    [Fact]
    public void RejectsNonPe() =>
        Assert.Throws<SignatureException>(() => PeImage.Parse("not a pe"u8.ToArray(), "test"));
}

public class GameExeTests
{
    [Fact]
    public void ReadsVersionNextToExe()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ffxiv-signatures-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "ffxiv_dx11.exe");
            Assert.Null(GameExe.ReadVersion(exe));                      // ver 파일 없음
            File.WriteAllText(Path.Combine(dir, "ffxivgame.ver"), "2026.10.06.0000.0000" + Environment.NewLine);
            Assert.Equal("2026.10.06.0000.0000", GameExe.ReadVersion(exe));
            File.WriteAllText(Path.Combine(dir, "ffxivgame.ver"), "7.57");
            Assert.Null(GameExe.ReadVersion(exe));                      // 형식이 다름
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class SigValueTests
{
    // .text: 48 8D 0D <rel32> (lea rcx,[rip+x]) 48 B8 <imm64> (mov rax, imm64) / .data
    private static (PeImage Pe, SigPattern.Match Lea, SigPattern.Match Mov) Sample(int rel, ulong abs)
    {
        byte[] text = [0x48, 0x8D, 0x0D, .. BitConverter.GetBytes(rel), 0x48, 0xB8, .. BitConverter.GetBytes(abs)];
        var bytes = FakePe.Build((".text", text, FakePe.Code, 0), (".data", new byte[16], FakePe.Data, 0x1000));
        var target = FakePe.Target(bytes);
        return (target.Image, Assert.Single(SigPattern.Parse("48 8D 0D").Scan(target)), Assert.Single(SigPattern.Parse("48 B8").Scan(target)));
    }

    [Fact]
    public void RipRelativeAfterPattern()
    {
        // lea 끝(0x1007) + rel = .data 0x2010
        var (pe, lea, _) = Sample(0x2010 - 0x1007, 0);
        var v = SigValue.Resolve(pe, lea, offset: 0, rip: true);
        Assert.Equal((0x1003, (int?)0x2010, ".data", (string?)null), (v.ReadRva, v.ResolvedRva, v.Section, v.Error));
    }

    [Fact]
    public void NegativeOffsetReadsInsidePattern()
    {
        // 패턴 "48 8D 0D ? ? ? ? 48" (길이 8), offset -5 -> lea 의 rel32
        var (pe, _, _) = Sample(0x2010 - 0x1007, 0);
        var target = new ScanTarget(pe, "test");
        var m = Assert.Single(SigPattern.Parse("488D0D????????48", SigSyntax.Hex).Scan(target));
        var v = SigValue.Resolve(pe, m, offset: -5, rip: true);
        Assert.Equal(0x2010, v.ResolvedRva);
    }

    [Fact]
    public void AbsoluteAddressMinusImageBase()
    {
        var (pe, _, mov) = Sample(0, FakePe.ImageBase + 0x2008);
        var v = SigValue.Resolve(pe, mov, offset: 0, rip: false);
        Assert.Equal((0x2008, ".data"), (v.ResolvedRva, v.Section));
    }

    [Fact]
    public void ReportsOutsideImage()
    {
        var (pe, lea, mov) = Sample(0x7000_0000, 0x1234);
        var rip = SigValue.Resolve(pe, lea, 0, rip: true);
        Assert.Null(rip.Section);                  // 섹션 밖 — 엉뚱한 곳
        Assert.NotNull(rip.ResolvedRva);
        var abs = SigValue.Resolve(pe, mov, 0, rip: false);
        Assert.Null(abs.ResolvedRva);              // ImageBase 보다 작은 절대값
        Assert.NotNull(abs.Error);
        var far = SigValue.Resolve(pe, mov, 0x100, rip: true);
        Assert.NotNull(far.Error);                 // 읽을 자리가 파일 밖
    }

    [Fact]
    public void RipOffsetAddsTrailingImmediate()
    {
        var (pe, lea, _) = Sample(0x100, 0);
        Assert.Equal(0x1007 + 0x100 + 1, SigValue.Resolve(pe, lea, 0, rip: true, ripOffset: 1).ResolvedRva);
    }
}

public class SigPatternEdgeTests
{
    private static ScanTarget Target(byte[] text) => FakePe.Target(FakePe.Text(text));

    // 시험 데이터(20KB)보다 길어야 맞는 패턴도 모든 곳에 맞으면 거부 — 길이와 상관없이 구조로 가른다
    [Theory]
    [InlineData("(48 | ?) [16384] [16384]")]
    [InlineData("((48 | 4C) | ? ?) [16384] [8000]")]
    public void LongUniversalPatternsAreRejected(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<SignatureException>(() => SigPattern.Parse(text));
        Assert.Contains("모든 위치에 맞는", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
    }

    // 대안 양쪽에 다 고정 바이트가 있으면 모든 곳에 맞지 않는다
    [Fact]
    public void AlternativesWithFixedBytesOnEverySideAreAccepted()
    {
        Assert.Single(SigPattern.Parse("(48 ? | 4C) 8B").Scan(Target([0x90, 0x4C, 0x8B])));
        Assert.Empty(SigPattern.Parse("48 [16384] [16384] 8B").Scan(Target([0x48, 0x8B]))); // 길어도 고정 바이트가 있으면 받는다
    }

    // 가변 건너뛰기를 겹치면 거부 (되짚기가 폭의 곱만큼). 건너뛰기 하나의 최대 폭 안이면 받는다
    [Fact]
    public void OverlappingVariableSkipsAreRejected()
    {
        var ex = Assert.Throws<SignatureException>(() => SigPattern.Parse("48 [0-16384] [0-16384] DE AD BE EF"));
        Assert.Contains("겹친", ex.Message);
        var sig = SigPattern.Parse("48 [0-64] 8B [0-64] DE");
        var m = Assert.Single(sig.Scan(Target([0x48, 1, 2, 0x8B, 3, 0xDE])));
        Assert.Equal(6, m.Length);
        Assert.Equal(3, Assert.Single(SigPattern.Parse("48 [0-3] 8B").Scan(Target([0x48, 0, 0x8B, 0x8B]))).Length); // non-greedy
    }

    // 일치마다는 빨라도 스캔 전체가 제한을 넘으면 SignatureException
    [Fact]
    public void WholeScanTimeLimitThrowsSignatureException()
    {
        var target = Target(Enumerable.Repeat((byte)0xCC, 0x100).ToArray());
        var sig = SigPattern.Parse("CC");
        Assert.Equal(0x100, sig.Scan(target).Count);
        var ex = Assert.Throws<SignatureException>(() => sig.Scan(target, TimeSpan.Zero));
        Assert.Contains("끝나지 않아", ex.Message);
    }

    [Fact]
    public void NullArgumentsAreArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SigPattern.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => SigPattern.Parse(null!, SigSyntax.Hex));
        Assert.Throws<ArgumentNullException>(() => SigPattern.PeliteToHex(null!));
        Assert.False(SigPattern.TryParse(null!, out var p));
        Assert.Null(p);
        Assert.Throws<ArgumentNullException>(() => PeImage.Parse(null!, "test"));
        Assert.Throws<ArgumentNullException>(() => GameExe.ReadVersion(null!));
    }

    // 선택되지 않은 대안 쪽의 북마크는 0
    [Fact]
    public void UnselectedBookmarkIsZero()
    {
        var m = Assert.Single(SigPattern.Parse("(48 ' | 4C) 8B").Scan(Target([0x4C, 0x8B])));
        Assert.Equal([0], m.Saves);
        m = Assert.Single(SigPattern.Parse("(48 ' | 4C) 8B").Scan(Target([0x48, 0x8B])));
        Assert.Equal([FakePe.TextRva + 1], m.Saves);
    }

    // OverlayPlugin / cactbot: 덩어리 안에서 search_offset < 0x10000 - L - max(0, offset) - (rip ? 4 : 8) 인 일치만 찾는다
    [Fact]
    public void MissedByChunkedScan()
    {
        // L = 8, rip: 0xFFF0 + 8 + 4 = 0xFFFC < 0x10000 -> 찾음. offset 4 를 더하면 0x10000 -> 놓침
        var m = new SigPattern.Match(0xFFF0, [], 8);
        Assert.False(m.MissedByChunkedScan(0, rip: true));
        Assert.True(m.MissedByChunkedScan(4, rip: true));
        Assert.True(m.MissedByChunkedScan(0, rip: false));   // 8바이트 값: 0xFFF0 + 8 + 8 = 0x10000
        // 음수 offset(패턴 안을 읽음)은 0 으로 본다: 0xFFE0 + 8 + 0 + 8 = 0xFFF0
        var inside = new SigPattern.Match(0xFFE0, [], 8);
        Assert.False(inside.MissedByChunkedScan(-100, rip: false));
        Assert.True(inside.MissedByChunkedScan(16, rip: false));
        Assert.False(new SigPattern.Match(0x10000, [], 8).MissedByChunkedScan(0, rip: true)); // 다음 덩어리의 시작
    }

    [Fact]
    public void BadPathsAreSignatureExceptionOrNull()
    {
        Assert.Throws<SignatureException>(() => PeImage.Load(""));
        Assert.Null(GameExe.ReadVersion(""));
        Assert.Null(GameExe.ReadVersion("a\0b"));
    }

    // 파일 데이터가 없는 섹션(RawSize 0)의 PointerToRawData 는 보지 않는다
    [Fact]
    public void ZeroRawSizeSectionIgnoresPointerToRawData()
    {
        var pe = FakePe.Build((".text", [0x48, 0x8B], FakePe.Code, 0), (".bss", [], FakePe.Data, 0x100));
        BitConverter.GetBytes(0x7FFF0000).CopyTo(pe, 0x40 + 24 + 0xF0 + 40 + 20); // 둘째 섹션의 PointerToRawData
        var image = FakePe.Image(pe);
        Assert.Equal(0, image.Span(image.Require(".bss")).Length);
        Assert.Equal(".bss", image.SectionAt(0x2010)?.Name);
        Assert.Null(image.FileOffset(0x2010));
        Assert.Single(SigPattern.Parse("48 8B").Scan(new ScanTarget(image, "test", ScanScope.AllSections)));
    }
}
