using static ActPluginRepack.Tests.TestReports;

namespace ActPluginRepack.Tests;

// GUI 간편 모드(확인하고 고치기)의 판단: prepare 결과와 다시 읽은 상태 -> 무엇을 할지 (FixFlow)
public class FixFlowTests
{
    private static readonly Exception Network = new NetworkException("연결 실패");
    private static readonly Exception Throttled = new ThrottledException("막힘", "github.com", null);
    private static readonly Exception Limit = new GitHubLimitException("한도", null, false);
    private static readonly Exception FileError = new IOException("잠김");

    // prepare 결과만으로 멈추는 경우 (상태는 읽지 않는다)
    private static PrepareResult Case(string name) => name switch
    {
        // 후크가 깨짐: 고칠 자료를 받지 못한 탓인지 (받기 실패만 그 원인으로)
        "hooks-sig-network" => Prepared(hooksOk: false, signatureDownload: Network),
        "hooks-sig-throttled" => Prepared(hooksOk: false, signatureDownload: Throttled),
        "hooks-sig-limit" => Prepared(hooksOk: false, signatureDownload: Limit),
        "hooks-sig-file-error" => Prepared(hooksOk: false, signatureDownload: FileError),       // 받기 실패가 아님
        "hooks-no-cause" => Prepared(hooksOk: false),
        "hooks-opcode-network" => Prepared(hooksOk: false, opcodeDownload: Network),           // 다른 단계의 받기 실패는 원인이 아님
        "hooks-and-missing" => Prepared(hooksOk: false, missing: 1, opcodeProblem: "겹침"),   // 후크가 먼저
        // 시그니처 점검 자체가 실패
        "check-failed" => Prepared(signatures: false, checkFailed: true),
        "check-failed-sig-network" => Prepared(signatures: false, checkFailed: true, signatureDownload: Network), // 받기 실패가 먼저
        "check-failed-sig-file-error" => Prepared(signatures: false, checkFailed: true, signatureDownload: FileError),
        "check-failed-results-ok" => Prepared(hooksOk: true, checkFailed: true),
        // opcode 를 다 구하지 못함
        "opcode-network" => Prepared(missing: 1, opcodeDownload: Network),
        "opcode-throttled" => Prepared(missing: 2, opcodeDownload: Throttled),
        "opcode-limit" => Prepared(missing: 1, opcodeDownload: Limit),
        "opcode-problem" => Prepared(missing: 1, opcodeProblem: "값이 겹칩니다"),
        "opcode-file-error-problem" => Prepared(missing: 1, opcodeDownload: FileError, opcodeProblem: "값이 겹칩니다"),
        "opcode-file-error" => Prepared(missing: 1, opcodeDownload: FileError),               // 받기 실패가 아니고 까닭도 없음 -> 공개 전
        "opcode-not-published" => Prepared(missing: 1),
        "opcode-sig-network" => Prepared(missing: 1, signatureDownload: Network),             // 시그니처 단계의 받기 실패는 원인이 아님
        // 판단할 수 없음: 시그니처 점검 결과가 없으면 게임 파일을 읽지 못한 것, 있으면 일부만 공개
        "undecided-no-signatures" => Prepared(patchNeeded: null, signatures: false),
        "undecided-signatures" => Prepared(patchNeeded: null),
        "undecided-opcode-network" => Prepared(patchNeeded: null, opcodeDownload: Network),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("hooks-sig-network", "SignatureDownloadFailed")]
    [InlineData("hooks-sig-throttled", "SignatureDownloadFailed")]
    [InlineData("hooks-sig-limit", "SignatureDownloadFailed")]
    [InlineData("hooks-sig-file-error", "HooksUnfixable")]
    [InlineData("hooks-no-cause", "HooksUnfixable")]
    [InlineData("hooks-opcode-network", "HooksUnfixable")]
    [InlineData("hooks-and-missing", "HooksUnfixable")]
    [InlineData("check-failed", "SignatureCheckFailed")]
    [InlineData("check-failed-sig-network", "SignatureDownloadFailed")]
    [InlineData("check-failed-sig-file-error", "SignatureCheckFailed")]
    [InlineData("check-failed-results-ok", "SignatureCheckFailed")]
    [InlineData("opcode-network", "OpcodeDownloadFailed")]
    [InlineData("opcode-throttled", "OpcodeDownloadFailed")]
    [InlineData("opcode-limit", "OpcodeDownloadFailed")]
    [InlineData("opcode-problem", "OpcodeProblem")]
    [InlineData("opcode-file-error-problem", "OpcodeProblem")]
    [InlineData("opcode-file-error", "OpcodesNotPublished")]
    [InlineData("opcode-not-published", "OpcodesNotPublished")]
    [InlineData("opcode-sig-network", "OpcodesNotPublished")]
    [InlineData("undecided-no-signatures", "UndecidedGameFiles")]
    [InlineData("undecided-signatures", "UndecidedPartial")]
    [InlineData("undecided-opcode-network", "UndecidedPartial")]
    public void StopsOnPrepareResult(string name, string expected)
    {
        var r = Case(name);
        Assert.False(FixFlow.NeedsStatus(r)); // 멈추는 결론은 상태를 다시 읽지 않는다
        var d = FixFlow.Decide(r, null);
        Assert.Equal(Enum.Parse<FixAction>(expected), d.Action); // (internal 형식이라 이름으로 받는다)
        // 받지 못한 경우는 그 단계의 실패를 원인으로 (화면이 쉬운 말로 바꾼다)
        if (d.Action == FixAction.SignatureDownloadFailed) Assert.Same(r.SignatureDownloadFailure, d.Cause);
        else if (d.Action == FixAction.OpcodeDownloadFailed) Assert.Same(r.OpcodeDownloadFailure, d.Cause);
        else Assert.Null(d.Cause);
        Assert.Equal(d.Action == FixAction.OpcodeProblem ? r.OpcodeProblem : null, d.Problem);
    }

    // 멈추는 결론은 다시 읽은 상태가 무엇이든 같다 (ACT 에 더 새 플러그인이 있어도)
    [Fact]
    public void StopsIgnoreStatus()
    {
        var newer = Status(StatusStage.NewerInstalled);
        Assert.Equal(FixAction.UndecidedPartial, FixFlow.Decide(Case("undecided-signatures"), newer).Action);
        Assert.Equal(FixAction.OpcodesNotPublished, FixFlow.Decide(Case("opcode-not-published"), newer).Action);
        Assert.Equal(FixAction.HooksUnfixable, FixFlow.Decide(Case("hooks-no-cause"), Status(StatusStage.OfficialMatches, officialUpdate: true)).Action);
    }

    // 나아가는 결론: 다시 읽은 상태로 정한다. stage 가 null 이면 상태를 읽지 못함
    [Theory]
    // 패치 필요 없음: 더 새 플러그인 > 공식 최신본으로 바꿀 수 있음 > 할 일 없음
    [InlineData(false, null, false, true, "NothingToDo")]                // 상태를 읽지 못하면 바꾸지 않는다
    [InlineData(false, "OfficialMatches", true, true, "InstallOfficial")]
    [InlineData(false, "OfficialMatches", false, true, "NothingToDo")]
    [InlineData(false, "NewerInstalled", false, true, "NewerInAct")]
    [InlineData(false, "NewerInstalled", false, false, "NothingToDo")]   // ACT 를 모르면 더 새 것으로 보지 않는다
    [InlineData(false, "Stale", false, true, "NothingToDo")]
    [InlineData(false, "Patched", false, true, "NothingToDo")]
    // 고쳐야 함: 더 새 플러그인이 있으면 만들기 전에 멈춘다
    [InlineData(true, null, false, true, "Build")]
    [InlineData(true, "NewerInstalled", false, true, "NewerInAct")]
    [InlineData(true, "NotBuilt", false, true, "Build")]
    [InlineData(true, "NotInstalled", false, true, "Build")]
    [InlineData(true, "Patched", false, true, "Build")]                  // 이미 설치됐는지는 만든 뒤에 본다
    [InlineData(true, "OfficialMatches", true, true, "Build")]           // 고쳐야 하면 공식 최신본으로 바꾸지 않는다
    public void ProceedsOnStatus(bool patchNeeded, string? stage, bool officialUpdate, bool act, string expected)
    {
        var r = Prepared(patchNeeded: patchNeeded);
        Assert.True(FixFlow.NeedsStatus(r));
        var status = stage == null ? null : Status(Enum.Parse<StatusStage>(stage), act: act, officialUpdate: officialUpdate);
        var d = FixFlow.Decide(r, status);
        Assert.Equal(Enum.Parse<FixAction>(expected), d.Action);
        Assert.Same(status, d.Status); // 화면이 버전 등을 보일 때 쓰는 상태
        Assert.Null(d.Cause);
    }

    // 상태를 다시 읽어야 하는 결론은 패치 필요 없음 / 고쳐야 함뿐
    [Theory]
    [InlineData("PatchNeeded", true)]
    [InlineData("NotNeeded", true)]
    [InlineData("Undecided", false)]
    [InlineData("HooksBroken", false)]
    [InlineData("OpcodesMissing", false)]
    public void NeedsStatusOnlyWhenProceeding(string outcome, bool expected)
    {
        var r = Enum.Parse<PrepareOutcome>(outcome) switch
        {
            PrepareOutcome.PatchNeeded => Prepared(patchNeeded: true),
            PrepareOutcome.NotNeeded => Prepared(patchNeeded: false),
            PrepareOutcome.Undecided => Prepared(patchNeeded: null),
            PrepareOutcome.HooksBroken => Prepared(hooksOk: false),
            _ => Prepared(missing: 1),
        };
        Assert.Equal(outcome, r.Outcome.ToString());
        Assert.Equal(expected, FixFlow.NeedsStatus(r));
    }

    // 만든 뒤: 똑같은 패치본이 이미 ACT 에 있으면 설치하지 않는다 (읽지 못했거나 모르면 설치 — 설치가 같은 파일은 건너뛴다)
    [Theory]
    [InlineData(true, true, "AlreadyInstalled")]
    [InlineData(true, false, "Install")]
    [InlineData(true, null, "Install")]
    [InlineData(false, null, "Install")]
    public void AfterBuildInstallsUnlessAlreadyThere(bool readable, bool? ours, string expected)
    {
        var status = readable ? Status(StatusStage.Patched, installedIsOurs: ours) : null;
        Assert.Equal(Enum.Parse<FixInstall>(expected), FixFlow.AfterBuild(status));
    }
}
