// CLI 종료 코드 (build.ps1, GUI 의 관리자 권한 실행, 문서가 같은 값을 쓴다 — docs\CLI.md 의 표). 2·3 은 명령마다 뜻이 다르다:
// prepare / sigcheck 는 아래 HooksBroken·Incomplete, rsv 는 RsvConflicts, sigscan / sigregen --patterns 는 SigCommands 의 상수.
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int HooksBroken = 2;   // prepare / sigcheck: 패킷 후크가 깨졌는데 고치지 못함 (prepare 는 점검 실패 포함)
    public const int RsvConflicts = 2;  // rsv: 공식·직접 넣은 override 와 값이 다른 키가 있음
    public const int Incomplete = 3;    // prepare: opcode 를 못 구한 클라이언트가 있음
    public const int AppsRunning = 4;   // install / restore: ACT 나 게임이 실행 중
    public const int NotNeeded = 5;     // prepare: 공식 플러그인이 이미 지금 게임과 맞음
    public const int Undecided = 6;     // prepare: 패치가 필요한지 판단하지 못함
    public const int NewerPlugin = 7;   // install: ACT 에 더 새 플러그인이 있어 그대로 둠
    public const int Cancelled = 130;   // Ctrl+C 로 취소

    // 사용자용 오류(ToolException 등)로 끝났을 때의 코드 (관리자 권한으로 다시 실행한 쪽도 같은 코드)
    public static int Of(Exception ex) => ex switch
    {
        AppsRunningException => AppsRunning,
        NewerPluginInstalledException => NewerPlugin,
        _ => Error,
    };
}
