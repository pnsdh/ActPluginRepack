using System.Security.Principal;

// 관리자 권한. ACT 는 보통 Program Files (x86) 에 있어 플러그인 폴더에 쓰려면 관리자 권한이 필요하다.
// Core 는 권한이 모자라면 ElevationRequiredException 을 던지기만 하고, 다시 실행하는 건 프런트엔드(CLI/GUI)가 한다.
internal static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false; // ACT 는 Windows 전용
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // dir 에 파일을 만들 수 있는지 (실제로 시험해 본다 — ACL·UAC 가상화·읽기 전용을 모두 반영).
    // 만들어졌으면 쓸 수 있는 것 — 시험 파일을 바로 못 지우는 것(백신이 검사하느라 잡고 있음 등)은 쓰기 권한과 상관없다.
    public static bool CanWrite(string dir)
    {
        // 지난번에 (백신이 잡고 있어) 지우지 못한 시험 파일 정리 — ACT 폴더 등에 쌓이지 않게
        try
        {
            if (Directory.Exists(dir))
                foreach (string old in Directory.GetFiles(dir, ".actrepack-write-test-*"))
                    try { File.Delete(old); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        string probe = Path.Combine(dir, $".actrepack-write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
        for (int i = 0; i < 4; i++) // 지우기는 몇 번 시도만 (남아도 빈 파일 하나)
        {
            try { File.Delete(probe); break; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Thread.Sleep(50); }
        }
        return true;
    }

    // 쓸 수 없으면: 관리자가 아니면 ElevationRequiredException, 관리자인데도 못 쓰면 ToolException
    public static void EnsureWritable(string dir)
    {
        if (CanWrite(dir)) return;
        if (!IsElevated) throw new ElevationRequiredException(dir);
        throw new ToolException($"관리자 권한으로 실행했는데도 {dir} 에 쓸 수 없습니다. 파일이 잠겨 있거나 백신이 막고 있을 수 있습니다.");
    }
}

// 이 폴더에 쓰려면 관리자 권한으로 다시 실행해야 함
internal sealed class ElevationRequiredException(string dir)
    : Exception($"{dir} 에 쓰려면 관리자 권한이 필요합니다 (ACT 가 Program Files 안에 있는 경우)")
{
    public string Directory { get; } = dir;
}
