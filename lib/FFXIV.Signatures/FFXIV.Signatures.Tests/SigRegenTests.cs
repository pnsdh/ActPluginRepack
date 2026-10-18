namespace FFXIV.Signatures.Tests;

// 읽는 자리(offset)가 있는 시그니처의 재생성 — 작은 합성 exe (.text 에 함수 하나, .pdata, .data)
public class SigRegenTests
{
    // lea rcx,[rip+x] (x 는 .data) ; <middle> ; test eax,eax ; ret
    private static CodeImage Exe(TempFile file, byte[] middle, int dataOffset)
    {
        int leaEnd = FakePe.TextRva + 7;
        byte[] code = [0x48, 0x8D, 0x0D, .. BitConverter.GetBytes(0x2000 + dataOffset - leaEnd), .. middle, 0x85, 0xC0, 0xC3];
        byte[] pdata = [.. BitConverter.GetBytes(FakePe.TextRva), .. BitConverter.GetBytes(FakePe.TextRva + code.Length), 0, 0, 0, 0, .. new byte[12]];
        File.WriteAllBytes(file.Path, FakePe.Build((".text", code, FakePe.Code, 0), (".data", new byte[0x40], FakePe.Data, 0),
                                                   (".pdata", pdata, 0x40000040, 0)));
        return new CodeImage(file.Path);
    }

    private static readonly byte[] MovEdiEax = [0x8B, 0xF8];        // mov edi,eax (2바이트)
    private static readonly byte[] MovR8dEax = [0x44, 0x8B, 0xC0];  // mov r8d,eax (3바이트 — 같은 모양, 길이만 다름)

    // 읽는 필드(lea 의 RIP 변위) 뒤의 명령어 길이가 바뀌면 패턴 끝에서 잰 offset 도 바뀐다
    [Fact]
    public void OffsetFollowsTheSameFieldWhenLengthChanges()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, MovEdiEax, 0x10);
        var newExe = Exe(b, MovR8dEax, 0x20);
        // 488D0D????????8BF885C0 (11바이트), 읽는 자리 = 시작 + 3 = 끝 - 8
        var r = SigRegen.Regenerate(oldExe, newExe, SigPattern.HexToPelite("488D0D????????8BF885C0"), readOffset: -8);
        Assert.True(r.Pattern != null, r.Detail);
        Assert.Equal("488D0D????????448BC085C0", SigPattern.PeliteToHex(r.Pattern!));
        Assert.Equal(-9, r.Offset);
        Assert.Equal(0, r.RipOffset);
        Assert.StartsWith("rip disp of lea rcx", r.ReadField);

        var m = Assert.Single(SigPattern.Parse(r.Pattern!).Scan(newExe.Target));
        var v = SigValue.Resolve(newExe.Image, m, r.Offset!.Value, rip: true, r.RipOffset!.Value);
        Assert.Equal((0x2020, ".data"), (v.ResolvedRva, v.Section));
    }

    [Fact]
    public void UnchangedPatternKeepsOffsetAfterCheckingTheField()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, MovEdiEax, 0x10);
        var newExe = Exe(b, MovEdiEax, 0x30);
        var r = SigRegen.Regenerate(oldExe, newExe, SigPattern.HexToPelite("488D0D????????8BF885C0"), readOffset: -8);
        Assert.True(r.AlreadyMatches, r.Detail);
        Assert.Null(r.Pattern);
        Assert.Equal(-8, r.Offset);
    }

    // 읽는 자리가 필드 시작이 아니면 (offset 이 틀렸을 때) 만들지 않는다
    [Fact]
    public void RefusesWhenReadIsNotAFieldStart()
    {
        using TempFile a = new(), b = new();
        var r = SigRegen.Regenerate(Exe(a, MovEdiEax, 0x10), Exe(b, MovR8dEax, 0x20), SigPattern.HexToPelite("488D0D????????8BF885C0"), readOffset: -7);
        Assert.Null(r.Pattern);
        Assert.Null(r.Offset);
        Assert.Contains("시작이 아님", r.Detail);
    }
}

// 읽는 필드 뒤의 즉치 크기가 바뀌면(imm8 -> imm32) 명령어 모양은 같아도 RIP 기준점이 달라진다 — OldRipOffset 으로 알린다
public class SigRegenRipOffsetTests
{
    private static CodeImage Exe(TempFile file, byte[] cmp)
    {
        // cmp dword ptr [rip+x],imm ; test eax,eax ; ret   (x 는 .data)
        byte[] code = [.. cmp, 0x85, 0xC0, 0xC3];
        int end = FakePe.TextRva + cmp.Length;
        BitConverter.GetBytes(0x2000 + 0x10 - end).CopyTo(code, 2);
        byte[] pdata = [.. BitConverter.GetBytes(FakePe.TextRva), .. BitConverter.GetBytes(FakePe.TextRva + code.Length), 0, 0, 0, 0, .. new byte[12]];
        File.WriteAllBytes(file.Path, FakePe.Build((".text", code, FakePe.Code, 0), (".data", new byte[0x40], FakePe.Data, 0),
                                                   (".pdata", pdata, 0x40000040, 0)));
        return new CodeImage(file.Path);
    }

    [Fact]
    public void ReportsWhenTheBytesAfterTheRipDisplacementChange()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, [0x83, 0x3D, 0, 0, 0, 0, 0x05]);             // cmp dword ptr [rip+x],5 (imm8)
        var newExe = Exe(b, [0x81, 0x3D, 0, 0, 0, 0, 0x05, 0, 0, 0]);    // cmp dword ptr [rip+x],5 (imm32)
        var r = SigRegen.Regenerate(oldExe, newExe, "83 3D", readOffset: 0);
        Assert.True(r.Pattern != null, r.Detail);
        Assert.Equal(0, r.Offset);
        Assert.Equal((1, 4), (r.OldRipOffset, r.RipOffset));
    }
}

// call/jmp 대상(rel32)을 읽는 시그니처: OverlayPlugin 의 SigScan 은 rip=true, ripOffset 0 으로 읽는다 — RipOffset 도 0 으로 낸다
public class SigRegenRel32Tests
{
    [Fact]
    public void Rel32FieldHasZeroRipOffset()
    {
        using TempFile a = new();
        // call +2 ; test eax,eax ; test eax,eax ; ret
        byte[] code = [0xE8, 0x02, 0x00, 0x00, 0x00, 0x85, 0xC0, 0x85, 0xC0, 0xC3];
        byte[] pdata = [.. BitConverter.GetBytes(FakePe.TextRva), .. BitConverter.GetBytes(FakePe.TextRva + code.Length), 0, 0, 0, 0, .. new byte[12]];
        File.WriteAllBytes(a.Path, FakePe.Build((".text", code, FakePe.Code, 0), (".pdata", pdata, 0x40000040, 0)));
        var exe = new CodeImage(a.Path);
        var r = SigRegen.Regenerate(exe, exe, "E8 ? ? ? ? 85 C0", readOffset: -6);
        Assert.True(r.AlreadyMatches, r.Detail);
        Assert.Equal((-6, 0, 0), (r.Offset, r.RipOffset, r.OldRipOffset));
        Assert.StartsWith("rel32 of call", r.ReadField);
    }
}

// 위치 찾기·검증: 성공해야 하는 것(레지스터만 바뀜, call 대상 대응)과 실패해야 하는 것(엉뚱한 곳)
public class SigRegenPlacementTests
{
    private static CodeImage Exe(TempFile file, params FakePe.Fn[] fns)
    {
        File.WriteAllBytes(file.Path, FakePe.Functions(fns).Pe);
        return new CodeImage(file.Path);
    }

    private static int[] Starts(params FakePe.Fn[] fns) => FakePe.Functions(fns).Starts;

    // mov eax,[rcx+10] ; add eax,1 ; mov [rcx+14],eax ; ret  ->  같은 일을 rdx 로
    [Fact]
    public void RegisterChangeIsRegenerated()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, new FakePe.Fn([0x8B, 0x41, 0x10, 0x83, 0xC0, 0x01, 0x89, 0x41, 0x14, 0xC3]));
        var newExe = Exe(b, new FakePe.Fn([0x8B, 0x42, 0x10, 0x83, 0xC0, 0x01, 0x89, 0x42, 0x14, 0xC3]));
        var r = SigRegen.Regenerate(oldExe, newExe, "8B 41 10 83 C0 01 89 41");
        Assert.Equal("8B 42 10 83 C0 01 89 42", r.Pattern);
        Assert.False(r.CallTargetUnverified);
    }

    // 옛 패턴이 즉치를 와일드카드로 둔 명령어(값만 빼고 비교 — 명령어 표의 해시로 거른 뒤 문자열로 확인하는 길)
    [Fact]
    public void WildImmediateIsComparedWithoutItsValue()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, new FakePe.Fn([0x8B, 0x41, 0x10, 0x83, 0xC0, 0x01, 0x89, 0x41, 0x14, 0xC3]));
        var newExe = Exe(b, new FakePe.Fn([0x8B, 0x42, 0x10, 0x83, 0xC0, 0x07, 0x89, 0x42, 0x14, 0xC3]));
        Assert.Equal("8B 42 10 83 C0 ? 89 42", SigRegen.Regenerate(oldExe, newExe, "8B 41 10 83 C0 ? 89 41").Pattern);
    }

    // 이미 읽은 ScanTarget 으로 만든 CodeImage: 같은 대상을 쓰고, 명령어 모양은 디코드한 그대로
    [Fact]
    public void CodeImageFromScanTarget()
    {
        using TempFile a = new();
        File.WriteAllBytes(a.Path, FakePe.Functions(new FakePe.Fn([0x8B, 0x41, 0x10, 0x83, 0xC0, 0x01, 0xC3])).Pe);
        var target = new ScanTarget(a.Path);
        var exe = new CodeImage(target);
        Assert.Same(target, exe.Target);
        Assert.Equal(["Mov r4,mUInt32+10", "Add r4,#1", "Ret"], exe.FunctionTokens(FakePe.TextRva + 3));
        Assert.Throws<ArgumentException>(() => new CodeImage(new ScanTarget(a.Path, ScanScope.AllSections)));
    }

    // 진짜 위치는 구조체 offset 이 바뀌어(0x110 -> 0x118) 모양이 달라졌고, 옛 모양과 같은 코드는 패치 전부터 있던 다른 함수(B)에만
    // 남았다 — B 로 패턴을 만들면 다른 전역 변수를 읽는다. 옛 exe 에서도 B 에 맞으므로 거부해야 한다.
    [Fact]
    public void RefusesOtherCodeThatAlsoExistedInTheOldExe()
    {
        byte[] aOld = [0x48, 0x8B, 0x81, 0x10, 0x01, 0, 0, 0x48, 0x8B, 0x50, 0x20, 0x48, 0x8D, 0x0D, 0, 0, 0, 0, 0xC3];
        byte[] aNew = [0x48, 0x8B, 0x81, 0x18, 0x01, 0, 0, 0x48, 0x8B, 0x50, 0x20, 0x48, 0x8D, 0x0D, 0, 0, 0, 0, 0xC3];
        byte[] other = [0x4C, 0x8B, 0x81, 0x10, 0x01, 0, 0, 0x49, 0x8B, 0x50, 0x20, 0x48, 0x8D, 0x15, 0, 0, 0, 0, 0xC3];
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, new FakePe.Fn(aOld, Rip: [(14, 0x10)]), new FakePe.Fn(other, Rip: [(14, 0x80)]));
        var newExe = Exe(b, new FakePe.Fn(aNew, Rip: [(14, 0x10)]), new FakePe.Fn(other, Rip: [(14, 0x80)]));
        var r = SigRegen.Regenerate(oldExe, newExe, "48 8B 81 10 01 00 00 48 8B 50 20 48 8D 0D", readOffset: 0);
        Assert.Null(r.Pattern);
        Assert.Contains("옛 exe 의 다른 곳", r.Detail);
    }

    // 진짜 위치(C)가 바뀌어 사라졌고, 앞 두 명령어 모양만 같은 다른 함수(D)가 있다. 앞부분으로 줄인 모양에만 맞는 곳이나
    // 셋째 명령어 모양이 다른(cmp -> test) 곳으로 패턴을 만들면 안 된다.
    [Fact]
    public void RefusesPlaceThatMatchesOnlyAShortenedShape()
    {
        byte[] cOld = [0x48, 0x8B, 0x41, 0x30, 0x8B, 0x48, 0x08, 0x83, 0xF9, 0x05, 0xC3];
        byte[] cNew = [0x48, 0x8B, 0x41, 0x38, 0x8B, 0x48, 0x08, 0x83, 0xF9, 0x05, 0xC3];
        byte[] d = [0x48, 0x8B, 0x42, 0x30, 0x8B, 0x50, 0x08, 0x85, 0xD2, 0xC3];
        using TempFile a = new(), b = new();
        var r = SigRegen.Regenerate(Exe(a, new(cOld), new(d)), Exe(b, new(cNew), new(d)), "48 8B 41 30 8B 48 08 83 F9 05");
        Assert.Null(r.Pattern);
    }

    // call 형: mov rcx,rbx ; call T ; mov rcx,rax(-> rdx) ; test al,al. T 는 새 exe 에서 자리가 옮겨졌다
    private static readonly byte[] T = [0x8B, 0x41, 0x10, 0x83, 0xC0, 0x01, 0xC3];
    private static readonly byte[] U = [0x8B, 0x41, 0x18, 0xC3];
    private static readonly byte[] Pad = [0x90, 0xC3];
    private static FakePe.Fn Caller(byte reg, int target) =>
        new([0x48, 0x8B, 0xCB, 0xE8, 0, 0, 0, 0, 0x48, 0x8B, reg, 0x84, 0xC0, 0xC3], Calls: [(4, target)]);

    [Fact]
    public void CallTargetIsMappedAndVerified()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, Caller(0xC8, 1), new(T));
        FakePe.Fn[] fresh = [new(Pad), Caller(0xD0, 2), new(T)];
        var newExe = Exe(b, fresh);
        var r = SigRegen.Regenerate(oldExe, newExe, "E8 $ { ' } 48 8B C8 84 C0");
        Assert.Equal("E8 $ { ' } 48 8B D0 84 C0", r.Pattern);
        Assert.False(r.CallTargetUnverified, r.Detail);
        Assert.Equal(Starts(fresh)[2], Assert.Single(SigPattern.Parse(r.Pattern!).Scan(newExe.Target)).Saves[0]);
    }

    // 개수는 같게 맞지만 call 대상이 옛 대상(T)에 대응하지 않음(U) -> 이미 맞음이 아니다
    [Fact]
    public void SameCountButOtherCallTargetIsNotAlreadyMatching()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, Caller(0xC8, 1), new(T), new(U));
        var newExe = Exe(b, Caller(0xC8, 2), new(T), new(U));
        var r = SigRegen.Regenerate(oldExe, newExe, "E8 $ { ' } 48 8B C8 84 C0");
        Assert.False(r.AlreadyMatches);
        Assert.Null(r.Pattern);
        Assert.Contains("대응하지 않음", r.Detail);
    }

    // 대상과 지문·바이트가 똑같은 함수가 둘이면 어느 쪽이 대응하는지 모른다 -> 확인하지 못함으로
    [Fact]
    public void TiedFunctionMatchIsUnverified()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, Caller(0xC8, 1), new(T), new(T));
        var newExe = Exe(b, Caller(0xD0, 1), new(T), new(T));
        var r = SigRegen.Regenerate(oldExe, newExe, "E8 $ { ' } 48 8B C8 84 C0");
        Assert.NotNull(r.Pattern);
        Assert.True(r.CallTargetUnverified, r.Detail);
    }

    // 형식 오류·지원하지 않는 문법은 예외가 아니라 실패 Result 로
    [Theory]
    [InlineData("4C 8", "형식이 잘못")]
    [InlineData("48 [2] 8B", "건너뛰기")]
    [InlineData("48 ' 8B", "북마크")]
    [InlineData("(48 8B | 4C) 8B", "1바이트 고정값 대안")]
    [InlineData("? ? ?", "고정 바이트")]
    public void FormatErrorsAreResults(string pattern, string why)
    {
        using TempFile a = new();
        var exe = Exe(a, new FakePe.Fn(T));
        var r = SigRegen.Regenerate(exe, exe, pattern);
        Assert.Null(r.Pattern);
        Assert.False(r.AlreadyMatches);
        Assert.Contains(why, r.Detail);
    }

    // 명령어로 읽히지 않는 자리 / 파일 밖은 call 대상 없음 (예외 대신)
    [Fact]
    public void CallTargetOfUndecodableOrMissingPlaceIsNull()
    {
        using TempFile a = new();
        var exe = Exe(a, new FakePe.Fn([0x06, 0xC3])); // 0x06 은 64비트에서 잘못된 명령어
        Assert.Null(exe.CallTarget(FakePe.TextRva));
        Assert.Null(exe.CallTarget(0x9000));
    }
}

internal sealed class TempFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ffxiv-signatures-test-{Guid.NewGuid():N}.exe");
    public void Dispose() { try { File.Delete(Path); } catch (IOException) { } }
}
