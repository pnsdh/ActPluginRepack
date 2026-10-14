namespace ActPluginRepack.Tests;

// 출시 목록: Thaliak 의 처음 내려준 시각(first_offered)이 같은 패치 묶음이 출시 한 번 (실제 글로벌 기록)
public class ReleaseListTests
{
    private static readonly (string, string?)[] Global =
    [
        ("2025.06.28.0000.0001", "2026-07-28T07:15"), ("2026.07.16.0001.0000", "2026-07-28T07:15"),
        ("2026.07.16.0000.0001", "2026-08-07T07:08"), ("2026.08.05.0000.0000", "2026-08-07T07:08"),
        ("2026.07.07.0000.0001", "2026-09-09T05:13"), ("2026.08.19.0000.0000", "2026-09-09T05:13"), ("2026.09.01.0000.0000", "2026-09-09T05:13"),
        ("2025.09.09.0000.0001", "2026-09-17T07:31"), ("2026.08.31.0000.0001", "2026-09-17T07:31"), ("2026.09.15.0000.0000", "2026-09-17T07:31"),
        // 7.57: 미리 만든 09.29 / 10.02 를 10.06 과 함께 10-13 에 내려줌
        ("2025.09.30.0000.0001", "2026-10-13T05:51"), ("2026.09.29.0000.0000", "2026-10-13T05:51"),
        ("2026.10.02.0000.0000", "2026-10-13T05:51"), ("2026.10.06.0000.0000", "2026-10-13T05:51"),
    ];

    [Fact]
    public void BatchesBecomeReleases()
    {
        Assert.Equal(["2026.07.16.0001.0000", "2026.08.05.0000.0000", "2026.09.01.0000.0000", "2026.09.15.0000.0000", "2026.10.06.0000.0000"],
                     Thaliak.ReleasesOf(Global));
    }

    [Fact]
    public void PreviousReleaseSkipsPreReleasePatches()
    {
        var releases = Thaliak.ReleasesOf(Global);
        Assert.Equal("2026.09.15.0000.0000", Thaliak.PreviousOf(releases, "2026.10.06.0000.0000"));
        // Thaliak 이 아직 모르는 새 버전이어도 그 앞 출시는 안다
        Assert.Equal("2026.10.06.0000.0000", Thaliak.PreviousOf(releases, "2026.10.27.0000.0000"));
        Assert.Equal("2026.07.16.0001.0000", Thaliak.PreviousOf(releases, "2026.08.05.0000.0000"));
        Assert.Null(Thaliak.PreviousOf(releases, "2026.07.16.0001.0000"));
        // 안전장치로 한 출시 더 앞까지 (가까운 것부터)
        Assert.Equal(["2026.09.15.0000.0000", "2026.09.01.0000.0000"], Thaliak.EarlierOf(releases, "2026.10.06.0000.0000").Take(2));
    }

    // 출시를 처음 내려준 시각 (karashiiro 갱신 시각과 비교) — 버전 문자열의 날짜(10-06)가 아니라 실제 출시(10-13)
    [Fact]
    public void ReleaseTimeIsWhenTheBatchWasOffered()
    {
        var withTime = Global.Select(p => (p.Item1, p.Item2 == null ? null : p.Item2 + ":00Z"));
        Assert.Equal(new DateTime(2026, 10, 13, 5, 51, 0, DateTimeKind.Utc), Thaliak.ReleasedAtOf(withTime, "2026.10.06.0000.0000"));
        Assert.Null(Thaliak.ReleasedAtOf(withTime, "2026.10.27.0000.0000")); // 아직 모르는 버전
    }
}

// 직전 출시 -> 지금 대응표에 베이스 플러그인 값을 비춰 본 판정
public class LocalDiffJudgeTests
{
    // Machina 이름마다 100, 101, ... (베이스 플러그인 값)
    private static Dictionary<string, int> Base() =>
        OpcodeNames.Machina.Select((m, i) => (m.Name, Value: 100 + i)).DistinctBy(x => x.Name)
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

    private static Dictionary<int, int> Map(IEnumerable<(int Old, int New)> pairs) => pairs.ToDictionary(p => p.Old, p => p.New);

    [Fact]
    public void BaseForThePreviousReleaseIsTranslated()
    {
        var b = Base();
        var r = OpcodeCollector.JudgeLocalDiff(b, Map(b.Values.Select(v => (v, v + 400))));
        Assert.Equal(OpcodeCollector.LocalDiffVerdict.BaseIsPrevious, r.Verdict);
        Assert.All(b, kv => Assert.Equal(kv.Value + 400, r.Server![kv.Key]));
    }

    [Fact]
    public void BaseAlreadyForThisReleaseIsKept()
    {
        var b = Base();
        var r = OpcodeCollector.JudgeLocalDiff(b, Map(b.Values.Select(v => (v + 400, v))));
        Assert.Equal(OpcodeCollector.LocalDiffVerdict.BaseIsCurrent, r.Verdict);
        Assert.Equal(b, r.Server);
    }

    // opcode 가 바뀌지 않은 출시: 대응표가 그대로라 양쪽에 다 있다 — 바꿔도 같은 값
    [Fact]
    public void UnchangedOpcodesStayTheSame()
    {
        var b = Base();
        var r = OpcodeCollector.JudgeLocalDiff(b, Map(b.Values.Select(v => (v, v))));
        Assert.Equal(OpcodeCollector.LocalDiffVerdict.BaseIsPrevious, r.Verdict);
        Assert.Equal(b, r.Server);
    }

    [Fact]
    public void NeitherSideOrAmbiguousIsNotTrusted()
    {
        var b = Base();
        // 하나가 어느 쪽에도 없음 (대응표 오류)
        var missing = Map(b.Values.Skip(1).Select(v => (v, v + 400)));
        Assert.Equal(OpcodeCollector.LocalDiffVerdict.Unknown, OpcodeCollector.JudgeLocalDiff(b, missing).Verdict);
        // 양쪽 다 맞는데 바꾼 값이 다름 (판단 불가)
        var both = Map(b.Values.Select(v => (v, v + 1)).Append((99, 100)));
        Assert.Equal(OpcodeCollector.LocalDiffVerdict.Unknown, OpcodeCollector.JudgeLocalDiff(b, both).Verdict);
    }
}
