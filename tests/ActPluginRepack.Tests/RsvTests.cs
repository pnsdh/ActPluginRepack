namespace ActPluginRepack.Tests;

// ACT 로그의 RSV(262 줄) 가져오기: 공식 플러그인과 겹치지 않게, 한 파일 안에 키가 한 번씩만
public class RsvTests
{
    private const string KeyA = "_rsv_50001_-1_6_0_0_SE2DC5B04_EE2DC5B04";
    private const string KeyB = "_rsv_50002_-1_6_0_0_SE2DC5B04_EE2DC5B04";
    private const string KeyC = "_rsv_50003_-1_6_0_0_SE2DC5B04_EE2DC5B04";
    private const string KeyD = "_rsv_50004_-1_6_0_0_SE2DC5B04_EE2DC5B04";

    private static string Line(string key, string value) => $"262|2026-10-14T21:01:17.3140000+09:00|ko|00000009|{key}|{value}|98a430403356fc07";

    [Fact]
    public void ImportsOnlyWhatTheOfficialPluginLacks()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        string log = tmp.File("logs/Network_1.log", string.Join("\n",
            "00|2026-10-14T21:00:00|...",
            Line(KeyA, "재구성"),          // 공식에 같은 값
            Line(KeyB, "알테마 블래스터"),  // 공식에 다른 값
            Line(KeyC, "새 스킬"),          // 새 것
            Line(KeyC, "새 스킬"),          // 같은 줄 반복
            Line(KeyD, "대사\\n둘째 줄"),    // 줄바꿈 - 이름이 아님
            "262|broken"));
        var official = new Dictionary<string, string> { [KeyA] = "재구성", [KeyB] = "맥시멈" };

        var r = Rsv.Import(ws, [log], official);
        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.AlreadyOfficial);
        Assert.Single(r.Conflicts, c => c.Contains(KeyB));
        var lines = Rsv.Parse(File.ReadAllText(Path.Combine(ws.OverridesDir, Rsv.FileName)));
        Assert.Equal([(KeyC, "새 스킬")], lines);
        Assert.Equal($@"새로 1개, 공식 플러그인에 이미 있음 1개, 값이 달라 넣지 않음 1개 -> input\overrides\{Rsv.FileName} (1개)", r.Summary);
    }

    [Fact]
    public void DropsEntriesTheOfficialPluginCaughtUpWith()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        // 예전 가져오기 결과 (같은 키가 두 줄 - 예전 파일이 잘못돼 있어도 하나로)
        tmp.File(@"input/overrides/" + Rsv.FileName, $"{KeyA}|재구성\n{KeyC}|새 스킬\n{KeyC}|새 스킬\n");
        string log = tmp.File("logs/Network_1.log", Line(KeyC, "새 스킬"));
        var official = new Dictionary<string, string> { [KeyA] = "재구성" };

        var r = Rsv.Import(ws, [log], official);
        Assert.Equal(1, r.Removed);
        Assert.Equal(1, r.Total);
        Assert.Equal([(KeyC, "새 스킬")], Rsv.Parse(File.ReadAllText(r.FilePath!)));

        // 공식이 전부 따라잡으면 파일을 지운다 (빈 override 는 빌드가 거부)
        var all = Rsv.Import(ws, [log], new Dictionary<string, string> { [KeyA] = "재구성", [KeyC] = "새 스킬" });
        Assert.Null(all.FilePath);
        Assert.False(File.Exists(Path.Combine(ws.OverridesDir, Rsv.FileName)));
        Assert.Equal("새로 0개, 공식 플러그인에 이미 있음 1개, 공식에 들어가 정리 1개 - 넣을 것 없음", all.Summary);
    }

    // 직접 넣은 override 파일로는 빌드가 넣는 것(global_*.txt)만 본다 — 다른 이름의 파일과 값이 달라도 충돌이 아니다
    [Fact]
    public void OnlyGlobalOverrideFilesCountAsOtherInputs()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        tmp.File("input/overrides/global_mine.txt", $"{KeyA}|직접 넣은 값\n");
        tmp.File("input/overrides/notes.txt", $"{KeyB}|메모일 뿐\n");          // 빌드가 넣지 않는 파일
        string log = tmp.File("logs/Network_1.log", string.Join("\n", Line(KeyA, "로그 값"), Line(KeyB, "새 스킬")));

        var r = Rsv.Import(ws, [log], new Dictionary<string, string>());
        Assert.Contains(KeyA, Assert.Single(r.Conflicts));                    // global_ 파일과 다름 -> 그 파일 값을 둔다
        Assert.Equal(1, r.Added);
        Assert.Equal([(KeyB, "새 스킬")], Rsv.Parse(File.ReadAllText(r.FilePath!)));
    }

    // 같은 키가 로그마다 다르면 가장 최근 로그의 값을 쓰고, 충돌로 세지 않는다 (참고로만 알림)
    [Fact]
    public void DifferencesBetweenLogsAreNotConflicts()
    {
        using var tmp = new TempDir();
        var ws = new Workspace(tmp.Path);
        string older = tmp.File("logs/Network_1.log", Line(KeyC, "옛 이름"));
        string newer = tmp.File("logs/Network_2.log", Line(KeyC, "새 이름"));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));

        var r = Rsv.Import(ws, [older, newer], new Dictionary<string, string>()); // 준 순서와 상관없이 최근 로그 우선
        Assert.Empty(r.Conflicts);
        Assert.Equal([(KeyC, "새 이름")], Rsv.Parse(File.ReadAllText(r.FilePath!)));
    }

    [Fact]
    public void BuildRejectsDuplicateKeysInOneOverrideFile()
    {
        using var tmp = new TempDir();
        tmp.File("overrides/global_mine.txt", $"{KeyA}|하나\n{KeyA}|둘\n");
        var ex = Assert.Throws<ToolException>(() => Repacker.Execute(
            pluginPath: tmp.File("plugin/FFXIV_ACT_Plugin.dll", "not a plugin"),
            outputPath: Path.Combine(tmp.Path, "out", "FFXIV_ACT_Plugin.dll"),
            opcodesDir: null, overridesDir: Path.Combine(tmp.Path, "overrides"), dataDir: null, signaturesDir: null));
        Assert.Contains("같은 키", ex.Message);
    }
}
