// GUI 간편 모드(확인하고 고치기)의 판단: prepare 결과와 그 뒤 다시 읽은 ACT 상태로 무엇을 할지.
// 화면은 이 결론을 문구·단계 표시로 바꾸기만 한다 (판단을 시험할 수 있게 여기에).

internal enum FixAction
{
    SignatureDownloadFailed, // 후크가 깨졌는데 고칠 자료(upstream 시그니처, 옛 exe)를 받지 못함 (Cause)
    SignatureCheckFailed,    // 게임 파일을 읽지 못해 후크 점검 자체가 실패함
    HooksUnfixable,          // 후크가 깨졌는데 자동으로 고치지 못함
    OpcodeDownloadFailed,    // opcode 출처를 받지 못함 (Cause)
    OpcodeProblem,           // 받았지만 값이 겹치거나 설정이 틀려 쓰지 못함 (Problem)
    OpcodesNotPublished,     // opcode 가 아직 공개 전
    UndecidedGameFiles,      // 판단할 수 없음 — 게임 파일을 읽지 못해 후크 점검을 끝내지 못함
    UndecidedPartial,        // 판단할 수 없음 — 공개된 업데이트 정보가 아직 일부만 있음
    NewerInAct,              // ACT 에 받아 둔 공식 플러그인보다 새 플러그인이 있음 (Status) — 덮어쓰지 않는다
    InstallOfficial,         // 공식 최신본이 이미 게임과 맞는데 ACT 에는 다른 것이 있음 (Status) — 공식 최신본을 설치
    NothingToDo,             // 공식 최신본이 게임과 맞고 ACT 에도 그것이 있음 (상태를 읽지 못했을 때도)
    Build,                   // 고친 플러그인을 만든다
}

// Cause: 받지 못한 까닭 (화면이 쉬운 말로 바꾼다), Problem: opcode 를 쓰지 못한 까닭, Status: 판단에 쓴 ACT 상태
internal sealed record FixDecision(FixAction Action, Exception? Cause = null, string? Problem = null, StatusReport? Status = null);

// 만든 뒤: 방금 만든 것과 똑같은 패치본이 이미 ACT 에 있는지
internal enum FixInstall { AlreadyInstalled, Install }

internal static class FixFlow
{
    // 멈추지 않고 나아가는 결론(패치 필요 없음 / 고쳐야 함)이면 ACT 상태를 다시 읽어 정한다 — 멈추는 결론은 prepare 결과만으로
    public static bool NeedsStatus(PrepareResult r) => r.Outcome is PrepareOutcome.NotNeeded or PrepareOutcome.PatchNeeded;

    // status: prepare 뒤에 다시 읽은 상태 (읽지 못하면 null — 그때는 바꾸지 않는 쪽으로). NeedsStatus 가 아니면 쓰지 않는다.
    public static FixDecision Decide(PrepareResult r, StatusReport? status)
    {
        switch (r.Outcome)
        {
            // 후크(패킷을 받는 부분) 점검: 점검 자체가 실패했거나, 깨졌는데 고치지 못함 — 이대로는 설치하지 않는다
            case PrepareOutcome.HooksBroken:
                // 고칠 자료를 받지 못한 탓이면 그 원인을 먼저 알린다 — 이 단계의 실패만
                // (CSV 등 다른 단계의 네트워크 실패를 "자동으로 고치지 못함"의 원인으로 돌리지 않게)
                if (Errors.IsDownloadFailure(r.SignatureDownloadFailure))
                    return new(FixAction.SignatureDownloadFailed, Cause: r.SignatureDownloadFailure);
                return new(r.SignatureCheckFailed ? FixAction.SignatureCheckFailed : FixAction.HooksUnfixable);

            case PrepareOutcome.OpcodesMissing:
                // opcode 출처를 받다 실패한 까닭으로 (다른 단계의 네트워크 실패를 "받지 못함"으로 돌리지 않게)
                if (Errors.IsDownloadFailure(r.OpcodeDownloadFailure))
                    return new(FixAction.OpcodeDownloadFailed, Cause: r.OpcodeDownloadFailure);
                // 받았지만 쓰지 못함 — 기다려도 같을 수 있으니 "공개 전"으로 안내하지 않는다
                if (r.OpcodeProblem is { } problem) return new(FixAction.OpcodeProblem, Problem: problem);
                return new(FixAction.OpcodesNotPublished);

            // 판단할 수 없으면 설치하지 않는다: 후크 상태를 모르거나(점검 실패), opcode 출처에 빠진 항목이 있어
            // 바뀌었는지 모르는 경우 — 확인 안 된 채로 덮어쓰면 공식본보다 나을 게 없거나 오히려 깨질 수 있다
            case PrepareOutcome.Undecided:
                return new(r.Signatures == null ? FixAction.UndecidedGameFiles : FixAction.UndecidedPartial);

            case PrepareOutcome.NotNeeded:
                // ACT 에 더 새 플러그인이 있으면 덮어쓰지 않는다
                if (NewerInAct(status)) return new(FixAction.NewerInAct, Status: status);
                // 공식 최신본이 이미 게임과 맞는데 ACT 에는 옛 공식본이나 예전 패치본이 있으면 공식 최신본으로 바꾼다
                // (공식 업데이트가 나온 걸 모르고 누른 경우 — 해루봇의 플러그인 업데이트와 같은 결과).
                // 상태를 읽지 못하면(잠시 잠김 등) 바꾸지 않고 "필요 없음"으로
                return status is { OfficialUpdate: true }
                    ? new(FixAction.InstallOfficial, Status: status)
                    : new(FixAction.NothingToDo, Status: status);

            default: // PatchNeeded
                // ACT 에 더 새 플러그인이 있으면 만들어도 설치하지 않는다 — 만들기 전에 알린다
                return NewerInAct(status) ? new(FixAction.NewerInAct, Status: status) : new(FixAction.Build, Status: status);
        }
    }

    // 만든 뒤 다시 읽은 상태로: 방금 만든 것과 똑같은 패치본이 이미 ACT 에 있으면 다시 설치하지 않는다
    // (설치와 같은 판단 — Install.PatchInPlace. 읽지 못하면 아니라고 보고 설치한다 — 설치가 같은 파일은 건너뛴다)
    public static FixInstall AfterBuild(StatusReport? status) =>
        status?.InstalledIsOurs == true ? FixInstall.AlreadyInstalled : FixInstall.Install;

    // ACT 에 받아 둔 공식 플러그인보다 새 플러그인이 있음 (해루봇의 한국 임시본 등)
    private static bool NewerInAct(StatusReport? status) => status is { Stage: StatusStage.NewerInstalled, Act: not null };
}
