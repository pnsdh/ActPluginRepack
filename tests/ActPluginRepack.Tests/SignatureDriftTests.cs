namespace ActPluginRepack.Tests;

// 조용한 어긋남: 아직 맞지만 직전 출시와 다르게 맞는 메모리 시그니처 (합성 PE — .text 0x1000, .data 0x2000)
public class SignatureDriftTests
{
    private const string Pattern = "48 8B 0D"; // mov rcx,[rip+x] — 플러그인은 바로 뒤의 변위로 x 를 읽는다

    // .text 에 mov rcx,[rip+x] 를 targets 의 수만큼 (x = 각 RVA), 사이는 int3
    private static ScanTarget Exe(params int[] targets)
    {
        var text = Enumerable.Repeat((byte)0xCC, 0x100).ToArray();
        for (int i = 0; i < targets.Length; i++)
        {
            int at = 0x10 + i * 0x20;
            byte[] ins = [0x48, 0x8B, 0x0D, .. BitConverter.GetBytes(targets[i] - (0x1000 + at + 7))];
            ins.CopyTo(text, at);
        }
        var pe = FakePeSections.Build((".text", text), (".data", new byte[0x100]));
        return new ScanTarget(PeImage.Parse(pe, "test"), "test");
    }

    private static string? Why(ScanTarget before, ScanTarget now) =>
        SignatureDrift.Why(SignatureKinds.Memory, before, SigPattern.Parse(Pattern).Scan(before), now, SigPattern.Parse(Pattern).Scan(now));

    [Fact]
    public void SameTargetIsFine() => Assert.Null(Why(Exe(0x2010), Exe(0x2010)));

    // 여러 곳에 맞아도 모두 같은 대상이면 첫 일치가 맞다 (ServerTime 처럼)
    [Fact]
    public void MoreMatchesOfTheSameTargetAreFine() => Assert.Null(Why(Exe(0x2010), Exe(0x2010, 0x2010)));

    // 대상이 갈리면 첫 일치가 맞는지 알 수 없다
    [Fact]
    public void NewDistinctTargetIsSuspicious() => Assert.Contains("1곳에서 2곳", Why(Exe(0x2010), Exe(0x2020, 0x2010)));

    // 데이터를 가리키던 것이 코드를 가리키면 다른 명령어에 맞은 것
    [Fact]
    public void SectionChangeIsSuspicious() => Assert.Contains(".data -> .text", Why(Exe(0x2010), Exe(0x1080)));

    // 메모리 시그니처가 가리키는 것은 읽는 값 — 여러 곳에 맞아도 같은 값이면 하나 (자동 복구가 한 곳에만 맞는 교체 시그니처를
    // "가리키는 곳 수가 다름"으로 버리지 않게). deucalion 은 패턴 위치 그대로.
    [Fact]
    public void MemoryTargetsAreReadValues()
    {
        var exe = Exe(0x2010, 0x2010, 0x2020);
        Assert.Equal([0x2010, 0x2020], Sigcheck.Points(SigPattern.Parse(Pattern), exe, SignatureKinds.Memory).Order());
        Assert.Equal(3, Sigcheck.Points(SigPattern.Parse(Pattern), exe, SignatureKinds.Deucalion).Count);
    }

    // 깨진 것(0곳)은 sigcheck 가 다룬다
    [Fact]
    public void BrokenIsNotDrift() => Assert.Null(Why(Exe(0x2010), Exe()));
}
