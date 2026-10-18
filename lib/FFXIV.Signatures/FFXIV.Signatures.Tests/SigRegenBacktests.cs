namespace FFXIV.Signatures.Tests;

// 실제 deucalion 수정 이력으로 SigRegen 을 검증한다. 게임 exe 가 필요해 저장소에는 넣을 수 없으므로
// 환경 변수로 위치를 알려 줄 때만 돈다:
//   FFXIV_SIGNATURES_BACKTEST_DIR   <패치 파일 버전>.exe 들이 있는 폴더 (예: 2025.06.28.0000.0001.exe — ActPluginRepack 의
//                                   fetch-exe --patch 로 받음)
// 7.57 RECV 는 snapshots\global\<버전>\ffxiv_dx11.exe 대신 같은 폴더의 2026.09.15.0000.0000.exe / 2026.10.06.0000.0000.exe 를 쓴다.
public class SigRegenBacktests
{
    public static TheoryData<string, string, string, string, string> Cases => new()
    {
        { "7.56h -> 7.57 RECV", "2026.09.15.0000.0000", "2026.10.06.0000.0000", "E8 $ { ' } 4C 8B 4F 10 8B 47 1C 45", "E8 $ { ' } 4C 8B 47 10 44 8B 4F 1C" },
        { "7.25h3 -> 7.30 SEND", "2025.06.28.0000.0001", "2025.07.25.0000.0001", "40 57 41 56 48 83 EC 38 48 8B F9 4C 8B F2", "40 53 56 48 83 EC 38 48 8B D9 48 8B F2 8B" },
        { "7.25h3 -> 7.30 CT", "2025.06.28.0000.0001", "2025.07.25.0000.0001", "E8 $ { ' } 41 83 C7 ? 41 81 FF", "E8 $ { ' } 41 83 C5 ? 49 8B FC" },
        { "7.30 -> 7.30h CT", "2025.07.25.0000.0001", "2025.08.07.0000.0001", "E8 $ { ' } 41 83 C5 ? 49 8B FC", "E8 $ { ' } 41 83 C7 ? 49 8B FD" },
        { "08.14 -> 09.09 CT", "2025.08.14.0000.0001", "2025.09.09.0000.0001", "E8 $ { ' } 41 83 C7 ? 49 8B FD", "E8 $ { ' } 41 83 C5 ? 49 8B FC" },
        { "09.09 -> 09.11 CT", "2025.09.09.0000.0001", "2025.09.11.0000.0001", "E8 $ { ' } 41 83 C5 ? 49 8B FC", "E8 $ { ' } 41 83 C7 ? 49 8B FD" },
        { "합성: 끝 위치 기준", "2025.07.25.0000.0001", "2025.08.07.0000.0001", "41 83 C5 ? 49 8B FC 41 81 FD", "41 83 C7 ? 49 8B FD 41 81 FF" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RegeneratesToSamePlacesAsRealFix(string name, string oldExe, string newExe, string oldPattern, string truth)
    {
        string? dir = Environment.GetEnvironmentVariable("FFXIV_SIGNATURES_BACKTEST_DIR");
        if (dir == null) Assert.Skip("FFXIV_SIGNATURES_BACKTEST_DIR 없음");
        string oldPath = Path.Combine(dir, oldExe + ".exe"), newPath = Path.Combine(dir, newExe + ".exe");
        if (!File.Exists(oldPath) || !File.Exists(newPath)) Assert.Skip($"{name}: exe 없음");

        var newImage = new CodeImage(newPath);
        var r = SigRegen.Regenerate(new CodeImage(oldPath), newImage, oldPattern);
        Assert.True(r.Pattern != null, $"{name}: {r.Detail}");

        // 시작점은 정답과 다를 수 있으니 call 대상, 아니면 시작 또는 끝 위치 집합으로 비교
        var mine = SigPattern.Parse(r.Pattern!).Scan(newImage.Target);
        var want = SigPattern.Parse(truth).Scan(newImage.Target);
        Assert.Equal(want.Count, mine.Count);
        if (want.All(m => m.Saves.Count > 0))
            Assert.Equal(want.Select(m => m.Saves[0]).Order(), mine.Select(m => m.Saves[0]).Order());
        else
            Assert.True(want.Select(m => m.Rva).Order().SequenceEqual(mine.Select(m => m.Rva).Order()) ||
                        want.Select(m => m.End).Order().SequenceEqual(mine.Select(m => m.End).Order()), name);
    }

    // 시그니처를 담은 함수를 새 exe 에서 찾지 못해 전역 변수 기준 대체 경로로 만드는 실제 사례 (본가가 손으로 고친 것).
    // 정답과 같은 패턴일 필요는 없고, 한 곳에만 맞아 같은 값(Want)을 읽으면 된다.
    public static TheoryData<string, string, string, string, int, int> GlobalCases => new()
    {
        { "7.25h3 -> 7.30 OverlayPlugin EnmityHud", "2025.06.28.0000.0001", "2025.07.25.0000.0001", "488B8F????????488B01FF50??4C8B1D", 0, 0x29102F8 },
        { "7.38 -> 7.4 cactbot kJobData", "2025.10.30.0000.0000", "2025.12.09.0000.0000", "488B3D????????33ED", -6, 0x298EF10 },
    };

    [Theory]
    [MemberData(nameof(GlobalCases))]
    public void RegeneratesThroughTheGlobalVariable(string name, string oldExe, string newExe, string oldHex, int offset, int want)
    {
        string? dir = Environment.GetEnvironmentVariable("FFXIV_SIGNATURES_BACKTEST_DIR");
        if (dir == null) Assert.Skip("FFXIV_SIGNATURES_BACKTEST_DIR 없음");
        string oldPath = Path.Combine(dir, oldExe + ".exe"), newPath = Path.Combine(dir, newExe + ".exe");
        if (!File.Exists(oldPath) || !File.Exists(newPath)) Assert.Skip($"{name}: exe 없음");

        var newImage = new CodeImage(newPath);
        var r = SigRegen.Regenerate(new CodeImage(oldPath), newImage, SigPattern.HexToPelite(oldHex), offset);
        Assert.True(r.Pattern != null, $"{name}: {r.Detail}");
        Assert.True(r.GlobalAnchors >= 2, r.Detail);
        var m = Assert.Single(SigPattern.Parse(r.Pattern!).Scan(newImage.Target));
        Assert.Equal(want, SigValue.Resolve(newImage.Image, m, r.Offset!.Value, rip: true, r.RipOffset!.Value).ResolvedRva);
    }

    // 옛 패턴이 같은 개수(1곳)로 다시 맞지만 다른 변수(EnvManager)를 읽는 함정: OverlayPlugin/cactbot JobGauge70 은 7.4 에서 0곳이 됐다가
    // 7.5 부터 다른 함수에서 1곳에 맞는다. "이미 맞음"이 아니라 근거가 가리키는 진짜 위치(정답 JobGauge74 의 값)로 다시 만들어야 한다.
    [Fact]
    public void RebuildsAMatchThatReadsAnotherVariable()
    {
        string? dir = Environment.GetEnvironmentVariable("FFXIV_SIGNATURES_BACKTEST_DIR");
        if (dir == null) Assert.Skip("FFXIV_SIGNATURES_BACKTEST_DIR 없음");
        string oldPath = Path.Combine(dir, "2025.10.30.0000.0000.exe"), newPath = Path.Combine(dir, "2026.04.21.0000.0000.exe");
        if (!File.Exists(oldPath) || !File.Exists(newPath)) Assert.Skip("exe 없음");

        var newImage = new CodeImage(newPath);
        var r = SigRegen.Regenerate(new CodeImage(oldPath), newImage, SigPattern.HexToPelite("488B3D????????33ED"), -6);
        Assert.True(r.ReadsOtherGlobal, r.Detail);
        Assert.False(r.AlreadyMatches);
        Assert.True(r.Pattern != null, r.Detail);
        var m = Assert.Single(SigPattern.Parse(r.Pattern!).Scan(newImage.Target));
        Assert.Equal(0x2A95990, SigValue.Resolve(newImage.Image, m, r.Offset!.Value, rip: true, r.RipOffset!.Value).ResolvedRva);
    }
}
