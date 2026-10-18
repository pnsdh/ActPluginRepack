namespace ActPluginRepack.Gui;

// 화면에 보이는 이름 (버튼, 진행 화면 제목). 안내 문장이 버튼을 가리킬 때도 이 이름을 쓴다 — 버튼 이름과 안내가 어긋나지 않게.
// XAML 에서는 {x:Static gui:Titles.App} 처럼 쓴다.
internal static class Titles
{
    public const string App = "FFXIV 플러그인 임시 패치";
    public static string AppWithVersion => $"{App} {AppInfo.DisplayVersion}"; // 창 제목 (어느 버전인지 보이게)
    public const string Fix = "확인하고 고치기";              // 첫 화면 주 버튼, 그 진행 화면 제목
    public const string Recheck = "다시 확인하기";            // 이미 패치했거나 필요 없을 때의 주 버튼 (하는 일은 Fix 와 같다)
    public const string OfficialUpdate = "공식 최신본으로 업데이트";
    public const string Restore = "공식 플러그인으로 되돌리기";
    public const string Selected = "선택한 작업";              // 고급 모드 [선택한 작업 실행]의 진행 화면 제목
    public const string GameLocation = "게임 위치 설정";       // 게임 위치 화면을 여는 버튼과 그 화면 제목
    public const string Log = "자세한 기록";                  // 진행 화면의 기록 펼치기 버튼
    public const string Help = "도움말";                        // 창 윗줄·첫 화면의 버튼과 그 화면 제목
    public const string Discord = "디스코드";                    // 문의·문제 보고 (도움말 화면)
    public const string SaveReport = "문제 보고용 기록 저장";   // 도움말 화면, 실패·경고로 끝난 진행 화면의 결과 아래
}

// 여러 화면이 같은 상황에서 쓰는 안내 문장 (같은 상황은 같은 말로)
internal static class Notes
{
    // ACT·게임을 꺼야 할 때 같이 붙인다
    public const string AlsoHaerubot = "(해루봇이 켜져 있으면 해루봇도 끄세요)";

    // ACT 가 켜져 있다고 할 때 같이 붙인다 (창을 닫아도 알림 영역에 남아 있을 수 있다)
    public const string ActInTray = "ACT 창이 보이지 않아도 작업 표시줄 오른쪽 아래 알림 영역(시계 옆)에 ACT 아이콘이 있으면 켜져 있는 것입니다.";

    // 설치까지 하는 버튼을 누르기 전
    public const string CloseAppsFirst = "시작하기 전에 ACT 와 게임을 끄세요. " + AlsoHaerubot;

    // 설치한 뒤 할 일 (해루봇 사용자가 대부분)
    public const string LaunchAct = "해루봇에서 'ACT 실행'을 누르세요. (해루봇 없이 쓰면 ACT 를 실행하세요)";

    // 막혔을 때 개발자에게 알리는 방법 (도움말 화면과 실패·경고로 끝난 진행 화면에 두 버튼이 다 있다)
    public const string ReportToDiscord =
        $"[{Titles.SaveReport}]을 눌러 만든 파일을 [{Titles.Discord}]에 올리고 어떤 문제인지 적어 주세요.";

    // 게임 파일을 읽지 못함 — 런처가 아직 업데이트 중일 수 있다 (게임만이 아니라 런처까지 꺼야 한다)
    public const string GameFilesBusy = "런처가 아직 게임을 업데이트하는 중일 수 있습니다. 게임과 런처를 모두 끈 뒤 다시 누르세요.";

    public const string NextUpdate = $"게임이 또 업데이트되면 [{Titles.Recheck}]를 누르세요.";

    // 해루봇의 플러그인 업데이트 (같은 동작을 한 이름으로)
    public const string HaerubotUpdate = "해루봇에서 FFXIV 플러그인을 업데이트";

    // 공식 플러그인이 이미 게임과 맞고 ACT 에도 그것이 있음 (첫 화면, 작업 결과)
    public const string NotNeededTitle = "지금은 패치가 필요 없습니다";
    public const string NotNeeded =
        "공식 플러그인이 지금 게임 버전과 이미 맞고, ACT 에도 그 공식 플러그인이 설치되어 있습니다. 따로 고칠 필요가 없습니다. " + NextUpdate;

    // 확인된 공개 자료 없이 정한 opcode 로 고침 (첫 화면, 작업 결과) — 다시 누를 계기를 남긴다
    public const string Unconfirmed =
        "이번 버전 것으로 확인된 공개 자료가 나오기 전에, 이 PC 에서 직접 구했거나 확인되지 않은 값으로 고쳤습니다. " +
        $"큰 패치에서는 가끔 틀릴 수 있으니, 공개 자료가 나온 뒤(보통 몇 시간 안)에 [{Titles.Recheck}]를 누르세요.";

    // ACT 에 받아 둔 공식 플러그인보다 새 플러그인이 있음 (첫 화면, 작업 결과). 버전을 모르면 null.
    // retry: 끝의 할 일 ("[다시 확인하기]를 누르세요." 등 — 그 화면에서 누를 것)
    public static string NewerPlugin(string? installed, string? ours, string retry) =>
        $"ACT 에는 이 프로그램이 받은 공식 플러그인{Paren(ours)}보다 새 플러그인{Paren(installed)}이 설치되어 있습니다 (해루봇의 한국 임시본 등). " +
        "이 프로그램은 그 플러그인을 덮어쓰지 않고 그대로 둡니다.\n" +
        $"ACT 가 제대로 동작하면 그대로 쓰세요. 동작하지 않으면 공식 플러그인이 갱신된 뒤(보통 하루 안) {retry}";

    private static string Paren(string? s) => s != null ? $"({s})" : "";
}
