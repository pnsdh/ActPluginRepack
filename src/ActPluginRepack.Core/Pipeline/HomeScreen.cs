// GUI 간편 모드 첫 화면의 판단: 지금 상태(StatusReport)로 어떤 안내를 보일지.
// 화면은 이 결론을 문구·버튼으로 바꾸기만 한다 (판단을 시험할 수 있게 여기에).

internal enum HomeStatus
{
    ConfigBroken,   // 설정 파일이 문법부터 깨져 읽을 수 없음 — 게임 위치를 다시 저장하게
    ConfigProblem,  // 설정한 게임 위치에 문제가 있음
    ActMissing,     // ACT 의 FFXIV 플러그인 위치를 정하지 못함 (ActProblem)
    Patched,        // ACT 에 지금 게임용 패치본이 설치됨 (Unconfirmed: 확인 안 된 opcode 로 고침)
    OfficialUpdate, // 공식 최신본이 게임과 맞는데 ACT 에는 다른 것이 있음 — 공식 최신본으로 바꿀 수 있음
    NewerInstalled, // ACT 에 받아 둔 공식 플러그인보다 새 플러그인이 있음
    NotNeeded,      // 공식 플러그인이 이미 게임과 맞고 ACT 에도 그것이 있음
    Default,        // 그 밖 (아직 확인 안 함 등) — 기본 안내
}

// CanRestore: [공식 플러그인으로 되돌리기] 버튼을 보일지
// ActProblem: ActMissing 일 때 그 까닭의 종류 (null 이면 ActError 문장을 그대로 — 종류를 모르는 실패)
internal sealed record HomeDecision(HomeStatus Status, bool CanRestore, ActProblem? ActProblem = null, bool Unconfirmed = false);

internal static class HomeScreen
{
    // configBroken: 설정 파일이 문법부터 깨졌는지 (설정 오류가 있을 때만 묻는다 — 파일을 읽는다)
    public static HomeDecision Decide(StatusReport s, Func<bool> configBroken)
    {
        // ACT 에 이 프로그램의 패치본이 있고, 받아 둔 공식 플러그인이 있을 때만
        bool canRestore = s.PatchInstalled && s.BasePluginVersion != null;
        if (s.ConfigError != null)
            return new(configBroken() ? HomeStatus.ConfigBroken : HomeStatus.ConfigProblem, canRestore);
        // ACT 에 FFXIV 플러그인이 등록돼 있지 않은 것만 "설치한 뒤"로 안내하고, 나머지(켜진 것이 여러 개, 파일이 없음,
        // ACT 설정 파일이 깨짐)는 그 이유를 보인다
        if (s.Act == null) return new(HomeStatus.ActMissing, canRestore, ActProblemOf(s.ActProblem, s.ActError));
        // 판단은 CLI status 와 같다 (StatusReport.Stage)
        return s.Stage switch
        {
            // 확인된 공개 자료 없이 정한 opcode 로 고쳤다 — 결과 화면을 닫은 뒤에도 다시 누를 계기를 남긴다
            StatusStage.Patched => new(HomeStatus.Patched, canRestore, Unconfirmed: s.Prepared!.Unconfirmed is { Count: > 0 }),
            StatusStage.OfficialMatches when s.OfficialUpdate => new(HomeStatus.OfficialUpdate, canRestore),
            StatusStage.NewerInstalled => new(HomeStatus.NewerInstalled, canRestore),
            StatusStage.OfficialMatches => new(HomeStatus.NotNeeded, canRestore),
            _ => new(HomeStatus.Default, canRestore),
        };
    }

    // ACT 를 찾지 못한 까닭의 종류: 종류가 없으면 — 까닭 문장도 없으면 등록돼 있지 않은 것, 있으면 그 문장(null)
    public static ActProblem? ActProblemOf(ActProblem? problem, string? reason) =>
        problem ?? (reason == null ? ActProblem.NotRegistered : null);
}
