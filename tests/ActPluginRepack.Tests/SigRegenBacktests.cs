namespace ActPluginRepack.Tests;

// 실제 deucalion 수정 이력으로 SigRegen 을 검증한다. 게임 exe 가 필요해 저장소에는 넣을 수 없으므로
// 환경 변수로 위치를 알려 줄 때만 돈다:
//   ACTREPACK_BACKTEST_DIR   <패치 파일 버전>.exe 들이 있는 폴더 (예: 2025.06.28.0000.0001.exe — fetch-exe --patch 로 받음)
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
        string? dir = Environment.GetEnvironmentVariable("ACTREPACK_BACKTEST_DIR");
        if (dir == null) Assert.Skip("ACTREPACK_BACKTEST_DIR 없음");
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
}
