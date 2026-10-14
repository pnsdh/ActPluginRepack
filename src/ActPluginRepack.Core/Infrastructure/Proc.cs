using System.Diagnostics;
using System.Text;

// 외부 프로그램 실행. 출력은 모아서 돌려주고, onLine 을 주면 줄마다 넘긴다(진행 표시용).
// 시간 제한을 넘기거나 현재 작업이 취소되면 프로세스 트리째 끝낸다.
internal static class Proc
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    // GitHub 토큰은 외부 도구(python, pip, cargo, git, radare2 …)에 넘기지 않는다 — 필요 없고, 남의 스크립트에 새어 나가지 않게.
    // GitHub 자신의 CLI(gh)에만 그대로 넘긴다 (gh auth token 은 GH_TOKEN 이 있으면 그걸 알려 준다). 직접 띄우는 IDA 도 뺀다.
    internal static readonly string[] SecretEnv = ["GITHUB_TOKEN", "GH_TOKEN"];

    // unsetEnv: 자식에게 넘기지 않을 환경 변수 (예: TERM — 터미널 흉내를 내는 셸에서 실행될 때). SecretEnv 는 따로 늘 뺀다.
    public static (int Code, string Stdout, string Stderr) Run(string exe, IEnumerable<string> args, string cwd, string? pathPrefix = null,
                                                               TimeSpan? timeout = null, Action<string>? onLine = null,
                                                               IEnumerable<string>? unsetEnv = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // 물려받은 입력(GUI 는 없음, Git Bash 는 콘솔이 아님) 대신 빈 입력
            CreateNoWindow = true, // GUI 에서 콘솔 창이 뜨지 않게
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (pathPrefix != null) psi.Environment["PATH"] = pathPrefix + ";" + Environment.GetEnvironmentVariable("PATH");
        bool gh = Path.GetFileNameWithoutExtension(exe).Equals("gh", StringComparison.OrdinalIgnoreCase);
        foreach (string name in (unsetEnv ?? []).Concat(gh ? [] : SecretEnv)) psi.Environment.Remove(name);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (stdout) stdout.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (stderr) stderr.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        if (!p.Start()) throw new ToolException($"{exe} 실행에 실패했습니다.");
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        WaitOrKill(p, timeout ?? DefaultTimeout, Path.GetFileName(exe));
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // 끝날 때까지 기다린다. 시간 초과 -> ToolException, 작업 취소 -> OperationCanceledException (둘 다 트리째 종료)
    public static void WaitOrKill(Process p, TimeSpan timeout, string what)
    {
        var token = Operation.Token;
        var deadline = DateTime.UtcNow + timeout;
        while (!p.WaitForExit(200))
        {
            if (token.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.WaitForExit(10_000);
                token.ThrowIfCancellationRequested();
                throw new ToolException($"{what} 이(가) {timeout.TotalMinutes:0}분 안에 끝나지 않아 중단했습니다.");
            }
        }
        p.WaitForExit(); // 비동기 출력 읽기 마무리
    }

    public static void Check((int Code, string Stdout, string Stderr) r, string what)
    {
        if (r.Code != 0) throw new ToolException($"{what} 이(가) 실패했습니다 (종료 코드 {r.Code}): {LastLine(r.Stderr + r.Stdout)}");
    }

    public static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
