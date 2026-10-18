namespace FFXIV.Signatures.Tests;

// 전역 변수 기준 대체 경로: 시그니처를 담은 함수는 바뀌어 위치를 찾지 못하지만, 같은 변수를 쓰는 다른 함수들은 그대로인 경우
public class SigRegenGlobalTests
{
    private static CodeImage Exe(TempFile file, params FakePe.Fn[] fns)
    {
        File.WriteAllBytes(file.Path, FakePe.Functions(fns).Pe);
        return new CodeImage(file.Path);
    }

    // sub rsp,X ; mov ecx,imm ; mov rax,[rip+G] ; add rsp,X ; ret — 패치로 X 와 imm 이 바뀌어 지문도 모양도 달라진다
    private static FakePe.Fn Target(byte frame, byte[] imm, int data) =>
        new([0x48, 0x83, 0xEC, frame, 0xB9, .. imm, 0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x48, 0x83, 0xC4, frame, 0xC3], Rip: [(12, data)]);

    // 같은 변수를 쓰는 다른 함수들 (패치에도 그대로): mov rcx,[rip+G] ; test rcx,rcx ; ret / mov eax,[rip+G] ; add eax,1 ; ret
    private static FakePe.Fn User1(int data) => new([0x48, 0x8B, 0x0D, 0, 0, 0, 0, 0x48, 0x85, 0xC9, 0xC3], Rip: [(3, data)]);
    private static FakePe.Fn User2(int data) => new([0x8B, 0x05, 0, 0, 0, 0, 0x83, 0xC0, 0x01, 0xC3], Rip: [(2, data)]);

    private static readonly byte[] OldImm = [0x11, 0x22, 0x33, 0x44], NewImm = [0x88, 0x77, 0x66, 0x55];
    private const string OldPattern = "B9 11 22 33 44 48 8B 05"; // 패턴 바로 뒤가 G 의 RIP 변위 (offset 0)

    [Fact]
    public void FindsTheMovedVariableThroughOtherFunctions()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, Target(0x28, OldImm, 0x10), User1(0x10), User2(0x10));
        var newExe = Exe(b, Target(0x38, NewImm, 0x40), User1(0x40), User2(0x40));
        var r = SigRegen.Regenerate(oldExe, newExe, OldPattern, readOffset: 0);
        Assert.True(r.Pattern != null, r.Detail);
        Assert.Equal(2, r.GlobalAnchors);
        Assert.Contains("전역 변수 기준", r.Detail);
        Assert.Equal((0, 0, 0), (r.Offset, r.RipOffset, r.OldRipOffset));

        var m = Assert.Single(SigPattern.Parse(r.Pattern!).Scan(newExe.Target));
        var v = SigValue.Resolve(newExe.Image, m, r.Offset!.Value, rip: true, r.RipOffset!.Value);
        Assert.Equal((FakePe.DataRva + 0x40, ".data"), (v.ResolvedRva, v.Section));
    }

    // 근거가 하나뿐이면 믿지 않는다
    [Fact]
    public void RefusesWithASingleAnchor()
    {
        using TempFile a = new(), b = new();
        var r = SigRegen.Regenerate(Exe(a, Target(0x28, OldImm, 0x10), User1(0x10)), Exe(b, Target(0x38, NewImm, 0x40), User1(0x40)),
                                    OldPattern, readOffset: 0);
        Assert.Null(r.Pattern);
        Assert.Null(r.GlobalAnchors);
        Assert.Contains("근거 함수가 1개뿐", r.Detail);
    }

    // 근거 함수들이 서로 다른 곳을 가리키면 실패
    [Fact]
    public void RefusesWhenAnchorsDisagree()
    {
        using TempFile a = new(), b = new();
        var r = SigRegen.Regenerate(Exe(a, Target(0x28, OldImm, 0x10), User1(0x10), User2(0x10)),
                                    Exe(b, Target(0x38, NewImm, 0x40), User1(0x40), User2(0x60)), OldPattern, readOffset: 0);
        Assert.Null(r.Pattern);
        Assert.Contains("서로 다른 곳", r.Detail);
    }

    // 쌍둥이: 변수 G 를 쓰는 함수들과 똑같은 모양으로 다른 변수 H 를 쓰는 함수들이 있고, 패치로 G 쪽만 바뀌었다.
    // 새 exe 에 남은 그 지문은 H 쪽뿐이라 지문만 보면 G 의 사용자가 H' 의 사용자와 짝지어진다 — 옛 exe 에서 지문이 하나뿐인
    // 함수만 근거로 쓰므로 실패해야 한다 (H' 를 G' 로 내면 안 됨)
    [Fact]
    public void TwinFunctionsUsingAnotherVariableAreNotAnchors()
    {
        using TempFile a = new(), b = new();
        byte[] yImm = [0xAA, 0xBB, 0xCC, 0xDD];
        // 바뀐 G 사용자 (nop 하나 더 — 지문이 달라짐)
        FakePe.Fn User1b(int data) => new([0x48, 0x8B, 0x0D, 0, 0, 0, 0, 0x48, 0x85, 0xC9, 0x90, 0xC3], Rip: [(3, data)]);
        FakePe.Fn User2b(int data) => new([0x8B, 0x05, 0, 0, 0, 0, 0x83, 0xC0, 0x01, 0x90, 0xC3], Rip: [(2, data)]);
        var oldExe = Exe(a, Target(0x28, OldImm, 0x10), User1(0x10), User2(0x10), User1(0x20), User2(0x20), Target(0x58, yImm, 0x20));
        var newExe = Exe(b, Target(0x38, NewImm, 0x40), User1b(0x40), User2b(0x40), User1(0x50), User2(0x50), Target(0x58, yImm, 0x50));
        var r = SigRegen.Regenerate(oldExe, newExe, OldPattern, readOffset: 0);
        Assert.Null(r.Pattern);
        Assert.Contains("짝이 분명한 것이 없음", r.Detail);
    }

    // 개수가 같아 "이미 맞음"인 패턴도 그 변수를 쓰는 함수들로 확인한다: 원래 자리는 바뀌어 사라지고 같은 바이트가 다른 함수(다른
    // 변수 H 를 읽음)에서 맞으면, 근거가 가리키는 진짜 위치(G')로 다시 만든다 (OverlayPlugin/cactbot JobGauge70 이 7.5 부터 이 모양)
    [Fact]
    public void AlreadyMatchingPatternThatReadsAnotherVariableIsRebuilt()
    {
        using TempFile a = new(), b = new();
        var oldExe = Exe(a, Target(0x28, OldImm, 0x10), User1(0x10), User2(0x10));
        var newExe = Exe(b, Target(0x38, NewImm, 0x40), User1(0x40), User2(0x40), Target(0x58, OldImm, 0x60));
        var r = SigRegen.Regenerate(oldExe, newExe, OldPattern, readOffset: 0);
        Assert.True(r.ReadsOtherGlobal, r.Detail);
        Assert.False(r.AlreadyMatches);
        Assert.True(r.Pattern != null, r.Detail);
        var m = Assert.Single(SigPattern.Parse(r.Pattern!).Scan(newExe.Target));
        Assert.Equal(FakePe.DataRva + 0x40, SigValue.Resolve(newExe.Image, m, r.Offset!.Value, rip: true, r.RipOffset!.Value).ResolvedRva);
    }

    // 근거가 지금 읽는 변수와 같으면 그대로 "이미 맞음"
    [Fact]
    public void AlreadyMatchingPatternThatAgreesWithTheAnchorsStays()
    {
        using TempFile a = new(), b = new();
        var r = SigRegen.Regenerate(Exe(a, Target(0x28, OldImm, 0x10), User1(0x10), User2(0x10)),
                                    Exe(b, Target(0x28, OldImm, 0x40), User1(0x40), User2(0x40)), OldPattern, readOffset: 0);
        Assert.True(r.AlreadyMatches, r.Detail);
        Assert.False(r.ReadsOtherGlobal);
    }

    // 읽는 자리를 모르면(readOffset 없음) 변수를 알 수 없으므로 대체 경로를 쓰지 않는다
    [Fact]
    public void NotUsedWithoutReadOffset()
    {
        using TempFile a = new(), b = new();
        var r = SigRegen.Regenerate(Exe(a, Target(0x28, OldImm, 0x10), User1(0x10), User2(0x10)),
                                    Exe(b, Target(0x38, NewImm, 0x40), User1(0x40), User2(0x40)), OldPattern);
        Assert.Null(r.Pattern);
        Assert.DoesNotContain("전역 변수", r.Detail);
    }
}
