// 이 PC 의 사용자별 폴더 (%LOCALAPPDATA%\ActPluginRepack). 작업 폴더와 상관없이 GUI / CLI 가 같이 쓴다:
//   기본 작업 폴더(GUI), github-token.bin, cache\http, update\(자동 업데이트 묶음), gui.json
internal static class AppPaths
{
    public static string UserRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ActPluginRepack");

    // GUI 의 화면 설정 (문제 보고용 기록 묶음도 읽는다)
    public static string GuiSettings => Path.Combine(UserRoot, "gui.json");
}
