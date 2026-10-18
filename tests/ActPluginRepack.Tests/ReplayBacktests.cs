namespace ActPluginRepack.Tests;

// 지난 패치 재현(Replay) — 옛 exe·공식 릴리스를 실제로 받고 prepare/build 를 돌려 출시마다 몇 분 걸리므로, 환경 변수
// ACTREPACK_REPLAY 가 있을 때만 돈다:
//   ACTREPACK_REPLAY=1               도구(python / radare2 / opcodediff-venv)를 작업 폴더에 새로 받음
//   ACTREPACK_REPLAY=<tools 폴더>     그 안의 도구를 복사해 씀 (시간 절약)
// 작업 폴더는 %TEMP%\actrepack-replay\ 아래(끝나면 지움). radare2 는 콘솔이 필요해 PowerShell / cmd 에서 dotnet test 를 돌린다.
// Http·GitHub 전역 상태와 HttpCache.Dir 을 쓰므로 Http 시험과 같은 묶음에서 하나씩.
[Collection("http")]
public class ReplayBacktests
{
    // 알려진 출시 (지역, 패치 번호, 출시 후 게임 버전 — Thaliak 출시 묶음의 가장 큰 버전). 7.5 부터 한국도 같은 버전
    [Theory]
    [InlineData("global", "7.55", "2026.07.16.0001.0000")]
    [InlineData("global", "7.56h", "2026.09.15.0000.0000")]
    [InlineData("global", "7.57", "2026.10.06.0000.0000")]
    [InlineData("korean", "7.56h", "2026.09.15.0000.0000")]
    [InlineData("korean", "7.57", "2026.10.06.0000.0000")]
    public void ReplayMatchesOfficialRelease(string region, string retail, string version)
    {
        string? env = Environment.GetEnvironmentVariable("ACTREPACK_REPLAY");
        if (string.IsNullOrEmpty(env)) Assert.Skip("ACTREPACK_REPLAY 없음");
        Http.ResetForTests();
        GitHub.ResetForTests();

        var r = Replay.Run(new ReplayOptions { Region = Region.Find(region)!, Version = version, ToolsDir = Directory.Exists(env) ? env : null });
        Replay.Report(r);

        Assert.True(r.Compared, $"{region} {retail}: 정답(공식 릴리스)을 고르지 못했습니다.");
        Assert.True(r.Pass, $"{region} {retail} ({version}, 베이스 {r.BaseTag}, 정답 {r.Reference}): 정답과 다릅니다\n" +
                            string.Join("\n", r.Checks.Where(c => !c.Pass).SelectMany(c => c.Details.Select(d => "    - " + d).Prepend(c.Line()))));
    }
}
