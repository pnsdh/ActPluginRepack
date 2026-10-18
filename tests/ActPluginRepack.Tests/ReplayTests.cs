namespace ActPluginRepack.Tests;

// 지난 패치 재현(Replay)의 순수한 부분: 플러그인 고르기, opcode 비교, 후크 시그니처 비교, 가리키는 곳 규칙
public class ReplayTests
{
    private static DateTime Utc(string s) => DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static GitHubRelease Rel(string tag, string? published, bool prerelease = false) =>
        new("ravahn/FFXIV_ACT_Plugin", tag, []) { PublishedAt = published != null ? Utc(published) : null, Prerelease = prerelease };

    // 실제 게시 시각 (2026 가을) + 시험판 하나, 게시 시각이 없는 것 하나 — 최신부터 (API 순서)
    private static readonly GitHubRelease[] Releases =
    [
        Rel("3.0.3.3", "2026-10-13T20:52:14Z"),
        Rel("3.0.3.2-beta", "2026-10-13T10:00:00Z", prerelease: true),
        Rel("3.0.3.1", "2026-09-17T21:03:23Z"),
        Rel("3.0.3.0", "2026-09-08T19:04:57Z"),
        Rel("3.0.2.9", null),
        Rel("3.0.2.8", "2026-08-14T23:41:22Z"),
    ];

    [Fact]
    public void PicksLastBeforeReleaseAndFirstAfter()
    {
        // 7.57 (최신 출시): 베이스는 출시 전 마지막, 정답은 출시 뒤 처음 (시험판은 건너뜀)
        var p = Replay.PickPlugins(Releases, Utc("2026-10-13T05:51:12Z"), nextReleasedAt: null);
        Assert.Equal(("3.0.3.1", "3.0.3.3", false), (p.Base.Tag, p.Reference?.Tag, p.ReferenceIsBase));

        // 7.56h: 정답은 다음 출시(7.57) 전에 나온 것
        p = Replay.PickPlugins(Releases, Utc("2026-09-17T07:31:15Z"), Utc("2026-10-13T05:51:12Z"));
        Assert.Equal(("3.0.3.0", "3.0.3.1", false), (p.Base.Tag, p.Reference?.Tag, p.ReferenceIsBase));
    }

    [Fact]
    public void NoNewReleaseBeforeNextPatchMeansBaseWasRight()
    {
        // 다음 출시까지 새 공식 릴리스가 없음 -> 베이스가 정답
        var p = Replay.PickPlugins(Releases, Utc("2026-09-09T05:13:55Z"), Utc("2026-09-17T07:31:15Z"));
        Assert.Equal(("3.0.3.0", "3.0.3.0", true), (p.Base.Tag, p.Reference?.Tag, p.ReferenceIsBase));
    }

    [Fact]
    public void NoReferenceYetForTheLatestRelease()
    {
        // 최신 출시 뒤에 아직 공식이 안 나옴 -> 정답 없음
        var p = Replay.PickPlugins(Releases, Utc("2026-10-20T05:00:00Z"), nextReleasedAt: null);
        Assert.Equal("3.0.3.3", p.Base.Tag);
        Assert.Null(p.Reference);
        Assert.False(p.ReferenceIsBase);
    }

    [Fact]
    public void GivenTagsWinAndUnknownTagsFail()
    {
        var p = Replay.PickPlugins(Releases, Utc("2026-10-13T05:51:12Z"), null, baseTag: "3.0.3.0", refTag: "3.0.3.3");
        Assert.Equal(("3.0.3.0", "3.0.3.3"), (p.Base.Tag, p.Reference?.Tag));
        // 베이스만 주면 정답은 출시 시각으로
        p = Replay.PickPlugins(Releases, Utc("2026-10-13T05:51:12Z"), null, baseTag: "3.0.3.0");
        Assert.Equal("3.0.3.3", p.Reference?.Tag);
        Assert.Throws<ToolException>(() => Replay.PickPlugins(Releases, Utc("2026-10-13T05:51:12Z"), null, baseTag: "9.9.9.9"));
        // 목록에 출시 전 릴리스가 없음
        Assert.Throws<ToolException>(() => Replay.PickPlugins(Releases, Utc("2026-01-01T00:00:00Z"), null));
    }

    [Fact]
    public void OpcodeDiffListsChangedAndOneSidedNames()
    {
        var ours = new Dictionary<string, int> { ["ActorCast"] = 0x100, ["ActorMove"] = 0x200, ["OnlyOurs"] = 1 };
        var reference = new Dictionary<string, int> { ["ActorCast"] = 0x100, ["ActorMove"] = 0x2AB, ["OnlyRef"] = 2 };
        var diffs = Replay.DiffOpcodes(ours, reference);
        Assert.Equal(["ActorMove: 우리 0x200 / 정답 0x2AB", "OnlyOurs: 우리 0x1 / 정답 없음", "OnlyRef: 우리 없음 / 정답 0x2"],
                     diffs.Select(d => d.Describe()));
        Assert.Empty(Replay.DiffOpcodes(ours, ours));
    }

    [Fact]
    public void ChangedHooksIgnoresSpacingAndSeesNewRuntimeRecv()
    {
        Replay.HookSig D(string name, string pattern) => new(name, SignatureSources.Deucalion, pattern);
        var before = new List<Replay.HookSig> { D("Recv", "E8 $ { ' } 4C 8B 4F 10"), D("Send", "40 53 48 83"), D("CreateTarget.Normal", "48 89 5C 24") };
        // 띄어쓰기만 다름 -> 같음
        Assert.Empty(Replay.ChangedHooks(before, [D("Recv", "E8 ${'} 4C8B4F10"), D("Send", "40 53 48 83"), D("CreateTarget.Normal", "48 89 5C 24")]));
        // 7.57 처럼 deucalion 은 그대로, Machina 런타임 RECV 가 생김 -> Recv 가 바뀐 것
        var after = before.Append(new Replay.HookSig("Recv", SignatureSources.Machina, "E8 $ { ' } 4C 8B 47 10")).ToList();
        Assert.Equal(["Recv"], Replay.ChangedHooks(before, after));
        // CreateTarget 후보가 바뀜 -> 묶음 이름으로
        Assert.Equal(["CreateTarget"], Replay.ChangedHooks(before, [before[0], before[1], D("CreateTarget.Normal", "48 89 6C 24")]));
    }

    [Fact]
    public void PointRuleFollowsSavesThenKind()
    {
        // 북마크/call 대상이 있으면 마지막 것
        Assert.Equal(0x5000, Sigcheck.PointOf(new SigPattern.Match(0x1000, [0x3000, 0x5000], Length: 8), SignatureKinds.Deucalion));
        Assert.Equal(0x5000, Sigcheck.PointOf(new SigPattern.Match(0x1000, [0x3000, 0x5000], Length: 8), SignatureKinds.Memory));
        // 없으면 deucalion 은 시작 위치, memory/oodle 은 끝 (플러그인이 바로 뒤를 읽음)
        Assert.Equal(0x1000, Sigcheck.PointOf(new SigPattern.Match(0x1000, [], Length: 8), SignatureKinds.Deucalion));
        Assert.Equal(0x1008, Sigcheck.PointOf(new SigPattern.Match(0x1000, [], Length: 8), SignatureKinds.Memory));
        Assert.Equal(0x1008, Sigcheck.PointOf(new SigPattern.Match(0x1000, [], Length: 8), SignatureKinds.Oodle));
    }

    [Fact]
    public void HookGroupDropsVariant()
    {
        Assert.Equal("CreateTarget", Replay.HookGroup("CreateTarget.Fallback"));
        Assert.Equal("Recv", Replay.HookGroup("Recv"));
    }
}
