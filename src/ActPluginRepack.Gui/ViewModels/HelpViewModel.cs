using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActPluginRepack.Gui.ViewModels;

// 자주 묻는 질문 하나 (도움말 화면에서 접어 둔다)
internal sealed record FaqItem(string Question, string Answer);

// 도움말: 문제 보고(기록 저장, 디스코드), 자주 묻는 질문, 이 프로그램의 버전
internal sealed partial class HelpViewModel : ObservableObject
{
    public string ReportHelp =>
        $"문제가 생기면 [{Titles.SaveReport}]을 누르세요. 작업 기록과 상태를 파일 하나로 묶어 바탕화면에 저장합니다. " +
        $"그 파일을 [{Titles.Discord}]에 올리고 어떤 문제인지 적어 주세요.";

    // ---- 문제 보고용 기록 ----
    [ObservableProperty] private string? _reportStatus;
    private readonly LatestOnly _report = new();

    [RelayCommand]
    private Task SaveReport() => ProblemReport.Run(_report, s => ReportStatus = s);

    [RelayCommand]
    private void OpenDiscord()
    {
        ProblemReport.OpenDiscord(s => ReportStatus = s);
    }

    // ---- 자주 묻는 질문 (README.md 의 "자주 묻는 질문"과 같은 문장 — 바꾸면 README 와 packaging\사용법.txt 도.
    //      README 의 "문제가 생겼어요"는 위 문제 보고 칸이 대신한다. 폴더는 README 와 달리 이 PC 의 실제 경로로) ----
    private static string AppFolder => AppContext.BaseDirectory.TrimEnd('\\', '/');

    public IReadOnlyList<FaqItem> Faq { get; } =
    [
        new("\"확인되지 않은 값으로 고쳤습니다\"라고 나와요.",
            "정상입니다. 공개 자료가 나오기 전에 이 PC 에서 직접 구한 값으로 고친 것이라 지금 그대로 쓰면 됩니다. " +
            $"큰 패치에서는 가끔 틀릴 수 있으니, 공개 자료가 나오는 몇 시간 뒤에 [{Titles.Recheck}]를 한 번 더 누르세요. " +
            "값이 바뀌었으면 확인된 값으로 다시 고칩니다."),
        new("\"아직 이번 업데이트 정보가 공개되지 않았습니다\"라고 나와요.",
            "패치에 필요한 정보는 보통 게임 업데이트 후 몇 시간 안에 공개됩니다. 잠시 후 다시 누르세요.\n" +
            "\"받지 못했습니다\"라고 나오면 인터넷 연결이나 서버 문제이니 안내대로 확인한 뒤 다시 누르세요."),
        new("ACT 를 켜도 아무것도 안 잡혀요.",
            $"첫 화면이 \"패치가 적용되어 있습니다\"인지 확인하세요. 아니면 [{Titles.Fix}]를 누르세요.\n" +
            "적용되어 있으면 게임과 ACT 를 모두 끈 뒤, 해루봇에서 'ACT 실행'을 누르고 게임을 다시 켜세요. " +
            $"그래도 안 되면 {Notes.ReportToDiscord}"),
        new("고친 뒤 게임이 튕기거나 이상해요.",
            $"첫 화면의 [{Titles.Restore}]를 누르세요. 고친 플러그인 대신 공식 플러그인을 설치합니다. " +
            "공식 플러그인이 갱신될 때까지는 ACT 의 일부 기능이 동작하지 않을 수 있습니다.\n" +
            Notes.ReportToDiscord),
        new("해루봇이 FFXIV 플러그인을 다시 받아 버렸어요.",
            $"{Notes.HaerubotUpdate}하면 고친 플러그인이 지워지고 공식 플러그인으로 바뀝니다. " +
            $"공식 업데이트가 아직 나오지 않았다면 [{Titles.Fix}]를 다시 누르세요. 공식 업데이트를 받은 것이면 그대로 쓰면 됩니다."),
        new("\"사용자 계정 컨트롤\" 창이 떠요.",
            "ACT 가 Program Files 에 있으면 ACT 의 파일을 바꿀 때(설치와 되돌리기) 관리자 권한이 필요해서 뜹니다. " +
            "[예]를 누르세요. [아니요]를 누르면 아무것도 바꾸지 않습니다."),
        new("공식 업데이트가 나오면 어떻게 하나요?",
            $"{Notes.HaerubotUpdate}하거나, 이 프로그램에서 [{Titles.Recheck}]를 누르세요. 어느 쪽이든 고친 플러그인이 공식 최신본으로 바뀝니다. " +
            "모르고 이 프로그램을 눌러도 괜찮습니다. 공식 플러그인이 이미 지금 게임과 맞으면 고친 플러그인 대신 공식 최신본을 설치합니다.\n" +
            "다음 게임 업데이트 전까지는 이 프로그램을 쓰지 않아도 됩니다."),
        new("한국과 글로벌 클라이언트 중 하나만 업데이트했어요.",
            "업데이트하지 않은 클라이언트는 빼고 확인하고, 결과에 알려 줍니다. " +
            "그 서버에서 플레이할 때는 런처로 업데이트한 뒤 다시 누르세요."),
        new($"[{Titles.Restore}] 버튼이 없어요.",
            $"ACT 에 이 프로그램이 고친 플러그인이 없는 경우입니다(설치한 적이 없거나, {Notes.HaerubotUpdate}해 " +
            "이미 공식 플러그인으로 바뀜). 따로 되돌릴 것이 없습니다."),
        new("\"백신이 격리했을 수 있습니다\"라고 나와요.",
            "ACT 플러그인은 게임 메모리를 읽는 프로그램이라 백신(Windows Defender 등)이 특히 deucalion 파일을 잘못 진단하곤 합니다.\n" +
            "Windows 보안 → 바이러스 및 위협 방지 → 보호 기록에서 그 항목을 복원하거나, " +
            "바이러스 및 위협 방지 설정 → 설정 관리 → 제외에 " +
            $"압축을 푼 폴더({AppFolder})와 작업 폴더({AppPaths.UserRoot})를 넣은 뒤 다시 누르세요. " +
            "없어진 플러그인 파일은 프로그램이 다시 받습니다."),
        new("프로그램이 켜지지 않아요.",
            ".NET 10 데스크톱 런타임이 있어야 켜집니다. 처음 켤 때 설치 안내 창이 뜨면 안내대로 설치하세요. " +
            "직접 받으려면 https://dotnet.microsoft.com/download/dotnet/10.0 에서 \".NET 데스크톱 런타임\"의 Windows x64 설치 파일을 받으세요.\n" +
            "\"Windows의 PC 보호\" 창이 뜨면 [추가 정보] → [실행]을 누르세요. 개인이 만든 프로그램이라 유료 코드 서명이 없어서 뜹니다."),
        new("오버레이(cactbot 등) 일부가 안 맞아요.",
            "해루봇은 켤 때마다 OverlayPlugin 이 쓰는 게임 정보 파일을 원래 것으로 되돌립니다. " +
            @"해루봇 대신 ACT 폴더(보통 C:\Program Files (x86)\Advanced Combat Tracker)의 Advanced Combat Tracker.exe 를 직접 실행해 보세요. " +
            "데미지 미터 등 ACT 본체는 상관없습니다."),
        new("FFLogs 에 올릴 수 있나요?",
            "아니요. 고친 플러그인은 옛 버전 번호를 그대로 쓰므로 FFLogs 가 옛 플러그인으로 보고 받지 않습니다. 공식 업데이트 전까지 쓰는 임시용입니다."),
        new("프로그램을 지우려면?",
            $"압축을 푼 폴더({AppFolder})를 지우세요. 받은 플러그인과 캐시는 {AppPaths.UserRoot} 에 있으니 함께 지우면 깨끗해집니다.\n" +
            $"고친 플러그인을 설치해 둔 상태라면 먼저 첫 화면의 [{Titles.Restore}]를 누르거나, {Notes.HaerubotUpdate}한 뒤에 지우세요."),
    ];

    // ---- 이 프로그램 ----
    public string VersionText => $"버전 {AppInfo.DisplayVersion}";

    [RelayCommand]
    private static void OpenRepo() => Shell.Open(AppInfo.ReleasesUrl);

    [RelayCommand]
    private void Back() => Navigator.ShowStart();
}
