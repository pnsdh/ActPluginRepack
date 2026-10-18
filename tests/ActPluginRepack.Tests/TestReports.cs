namespace ActPluginRepack.Tests;

// 판단 시험용 prepare 결과와 상태 (파일을 읽지 않는다 — 위치 인자가 많아 기본값을 채운다)
internal static class TestReports
{
    // 있지 않은 ACT 플러그인 위치 (판단은 이 파일을 읽지 않는다)
    public static readonly ActTarget Act = new(Path.Combine(Path.GetTempPath(), "actrepack-no-act", PluginRelease.PluginFile), true);

    // hooksOk=false: 시그니처 점검은 했는데 핵심 후크가 깨짐. signatures=false: 점검 결과 없음 (게임 파일을 읽지 못함 등)
    public static PrepareResult Prepared(bool? patchNeeded = true, bool hooksOk = true, bool signatures = true, bool checkFailed = false,
                                         int missing = 0, Exception? signatureDownload = null, Exception? opcodeDownload = null,
                                         string? opcodeProblem = null) =>
        new([], signatures ? new Sigcheck.Result(hooksOk, [], [], []) : null, [.. Enumerable.Repeat("korean", missing)], patchNeeded, "")
        {
            SignatureCheckFailed = checkFailed,
            SignatureDownloadFailure = signatureDownload,
            OpcodeDownloadFailure = opcodeDownload,
            OpcodeProblem = opcodeProblem,
        };

    // act=false: ACT 를 찾지 못함 (actProblem / actError 가 그 까닭)
    public static StatusReport Status(StatusStage stage = StatusStage.NotPrepared, bool act = true, string? configError = null,
                                      string? basePlugin = "3.0.3.3", bool officialUpdate = false, bool patchInstalled = false,
                                      bool? installedIsOurs = null, ActProblem? actProblem = null, string? actError = null,
                                      MaterialState? prepared = null) =>
        new([], configError, basePlugin, prepared, [], null, act ? Act : null, actError, installedIsOurs, null, [],
            officialUpdate, actProblem, null, null, patchInstalled, stage);

    // 마지막 prepare 기록 (unconfirmed: 확인 안 된 opcode 로 고친 지역이 있음)
    public static MaterialState Material(bool unconfirmed = false) =>
        new(DateTime.Now, "3.0.3.3", new(), new(), true, true, Unconfirmed: unconfirmed ? ["korean"] : null, Outcome: PrepareOutcome.PatchNeeded);
}
