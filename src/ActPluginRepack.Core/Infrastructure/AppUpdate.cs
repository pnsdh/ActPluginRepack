using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

// 이 프로그램 자동 업데이트 (GUI 창 위쪽 띠의 [지금 업데이트] — 모든 화면 공통).
//
//   1) 새 v 태그 릴리스의 ActPluginRepack-<버전>-win-x64.zip 과 .sha256, .sig 를 %LOCALAPPDATA%\ActPluginRepack\update\<버전>\ 에
//      받고 SHA256 을 맞춰 보고, .sig 의 서명을 내장 공개키로 확인한다 (publish.ps1 이 같이 만들어 올림 — 없거나 틀리면 업데이트하지 않음)
//   2) 임시 폴더에 풀어 구성(GUI exe, cli\ActPluginRepack.exe)과 GUI exe 의 버전을 확인
//   3) 지금 폴더에 바꿔 넣는다 (Apply):
//      a. 새 파일을 모두 <이름>.new 로 복사 (오래 걸리는 부분 — 아직 원래 파일은 그대로)
//      b. 이름만 빠르게 바꾼다: <이름> -> <이름>.old (실행 중인 exe 도 이름은 바꿀 수 있다), <이름>.new -> <이름>. GUI exe 는 맨 끝.
//      도중에 실패하면 되돌린다. 묶음에 없는 파일(사용자가 둔 것)은 건드리지 않는다.
//   4) GUI 가 새 exe 를 띄우고 끝낸다. 새 버전은 시작할 때 .old 를 지운다 (CleanupOld). 띄우지 못하면 되돌린다 (Revert).
//
// 바꿔 넣는 동안은 프로그램 폴더의 일지(.actrepack-update.json)에 어느 단계인지, 어느 파일을 비켜 뒀는지 적어 둔다.
// 되돌리기·정리는 일지에 적힌 파일만 건드린다 (사용자의 다른 *.old 는 그대로). 도중에 프로세스가 죽었으면(창 닫기, 정전)
// 다음 시작 때 일지대로 원래 파일로 되돌린다 — 새것과 옛것이 섞인 채로 두지 않는다.
//
// 1~2 가 끝나기 전에는 아무것도 바꾸지 않는다. 폴더에 쓸 수 없으면(Program Files 등) 시작하지 않는다.
// 시험용: 환경 변수 ACTREPACK_UPDATE_FEED = zip·.sha256·.sig 가 있는 폴더 — GitHub 대신 거기서 찾는다.
internal static class AppUpdate
{
    public const string GuiExe = "ActPluginRepack.Gui.exe";
    public static string? Feed => Environment.GetEnvironmentVariable("ACTREPACK_UPDATE_FEED") is { Length: > 0 } f ? f : null;

    public static string AssetName(Version v) => $"ActPluginRepack-{v.ToString(3)}-win-x64.zip";

    // 배포본 구성(cli\ 가 옆에 있음)일 때만 — 개발 빌드 폴더를 덮어쓰지 않게
    public static bool Supported(string appDir) => File.Exists(Path.Combine(appDir, "cli", "ActPluginRepack.exe"));

    public sealed record Package(Version Version, string Zip, string Sha256, string Signature);

    // 받을 파일 위치 (URL 또는 시험용 로컬 경로)
    public static Package Resolve(AppInfo.Update update)
    {
        string name = AssetName(update.Version);
        if (Feed is { } feed)
            return new Package(update.Version, Path.Combine(feed, name), Path.Combine(feed, name + ".sha256"), Path.Combine(feed, name + ".sig"));
        var release = GitHubRelease.ByTag(AppInfo.Repo, update.Tag)
                      ?? throw new ToolException($"{update.Tag} 릴리스를 찾지 못했습니다.");
        string zip = release.AssetUrl(n => n == name) ?? throw new ToolException($"{update.Tag} 릴리스에 {name} 파일이 없습니다.");
        string sha = release.AssetUrl(n => n == name + ".sha256")
                     ?? throw new ToolException($"{update.Tag} 릴리스에 {name}.sha256 파일이 없어 받은 파일을 확인할 수 없습니다.");
        string sig = release.AssetUrl(n => n == name + ".sig")
                     ?? throw new ToolException($"{update.Tag} 릴리스에 서명 파일({name}.sig)이 없어 업데이트하지 않습니다. [직접 받기]로 받으세요.");
        return new Package(update.Version, zip, sha, sig);
    }

    // 받은 업데이트 묶음을 푸는 곳 (%LOCALAPPDATA%\ActPluginRepack\update\<버전>). 시험은 임시 폴더로 바꾼다
    internal static string DownloadsDir { get; set; } = Path.Combine(AppPaths.UserRoot, "update");

    // 받아서 확인하고 푼다. 풀린 묶음의 맨 위 폴더(GUI exe 가 있는 곳)를 돌려준다.
    public static string Download(Package pkg, Action<double>? progress = null)
    {
        string dir = Path.Combine(DownloadsDir, pkg.Version.ToString(3));
        Files.ResetDirectory(dir);
        string zip = Path.Combine(dir, AssetName(pkg.Version));

        string expected = (ReadText(pkg.Sha256) ?? throw new ToolException("SHA256 파일을 받지 못했습니다."))
            .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (expected.Length != 64) throw new ToolException("SHA256 파일의 형식이 올바르지 않습니다.");
        Fetch(pkg.Zip, zip, progress);
        string actual = Files.Sha256Of(zip);
        if (actual != expected) throw new ToolException("받은 파일이 손상되었습니다 (SHA256 불일치). 다시 시도하세요.");
        // 배포하는 사람의 키로 서명한 묶음인지 (계정·토큰이 털려 zip 과 .sha256 이 함께 바뀐 경우를 막는다)
        string signature = ReadText(pkg.Signature) ?? throw new ToolException("서명 파일을 받지 못했습니다.");
        if (!UpdateSignature.Verify(AssetName(pkg.Version), actual, signature))
            throw new ToolException("받은 파일의 서명이 맞지 않아 업데이트하지 않습니다. 이 프로그램의 릴리스 페이지를 확인하세요.");

        string x = Path.Combine(dir, "x");
        ZipFile.ExtractToDirectory(zip, x);
        string root = Directory.GetFiles(x, GuiExe, SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                      ?? throw new ToolException($"받은 묶음에 {GuiExe} 파일이 없습니다.");
        if (!Supported(root!)) throw new ToolException(@"받은 묶음에 cli\ActPluginRepack.exe 파일이 없습니다.");
        string? fileVersion = FileVersionInfo.GetVersionInfo(Path.Combine(root!, GuiExe)).FileVersion;
        if (!Version.TryParse(fileVersion, out var v) || v.ToString(3) != pkg.Version.ToString(3))
            throw new ToolException($"받은 프로그램의 버전이 예상과 다릅니다 ({fileVersion}, 예상 {pkg.Version.ToString(3)}).");
        return root!;
    }

    // 바꿔 넣기 일지 (프로그램 폴더에). 경로는 모두 프로그램 폴더 기준 상대 경로.
    public const string JournalFile = ".actrepack-update.json";

    private const string Copying = "copying", Swapping = "swapping", Applied = "applied";

    private sealed class Journal
    {
        public string State { get; set; } = Copying;   // copying(.new 복사 중) / swapping(이름 바꾸는 중) / applied(끝남, .old 정리 전)
        public List<string> Items { get; set; } = [];   // 묶음의 파일 — 이것의 <이름>.new 는 우리 것
        public List<string> Replaced { get; set; } = []; // 원래 있던 것 — <이름>.old 로 비켜 둠 (되돌릴 때 제자리로)
        public List<string> Created { get; set; } = [];  // 새로 생긴 것 (되돌릴 때 지움)
        public List<string> Stale { get; set; } = [];    // 지난 업데이트가 남긴, 아직 못 지운 .old 파일 (지우기만)
    }

    // 시험용: 단계마다 불린다 ("copy:<경로>" / "swap:<경로>"). 여기서 던지면 도중에 프로세스가 죽은 것과 같다.
    internal static Action<string>? Step;

    // 지금 폴더에 바꿔 넣기 (실패하면 되돌림)
    public static void Apply(string staging, string appDir)
    {
        if (!Elevation.CanWrite(appDir))
            throw new ToolException($"프로그램 폴더({appDir})에 쓸 수 없습니다. 바탕화면이나 문서 폴더처럼 쓸 수 있는 곳에 두면 자동 업데이트를 쓸 수 있습니다.");
        var stale = Settle(appDir)
                    ?? throw new ToolException("지난 업데이트를 마저 되돌리지 못했습니다. 프로그램을 다시 시작한 뒤 다시 시도하세요.");
        // GUI exe 는 맨 끝에 — 도중에 멈춰도 실행할 exe 는 원래 것으로 남아 다음 시작 때 되돌릴 수 있게
        var items = Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staging, f))
            .OrderBy(rel => rel.Equals(GuiExe, StringComparison.OrdinalIgnoreCase)).ToList();
        var j = new Journal { State = Copying, Items = items, Stale = stale };

        // a. 새 파일을 <이름>.new 로 (원래 파일은 그대로)
        try
        {
            Save(appDir, j);
            foreach (string rel in items)
            {
                string target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(staging, rel), target + ".new", overwrite: true);
                if (File.Exists(target + ".old")) File.Delete(target + ".old"); // 비켜 둘 자리 (지금 있는 .old 는 우리 것이 된다)
                Step?.Invoke("copy:" + rel);
            }
            j.Stale.RemoveAll(rel => !File.Exists(Path.Combine(appDir, rel)));
            j.Replaced = items.Where(rel => File.Exists(Path.Combine(appDir, rel))).ToList();
            j.Created = items.Except(j.Replaced).ToList();
            j.State = Swapping;
            Save(appDir, j);
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            if (Undo(appDir, j)) Close(appDir, j.Stale);
            else TrySave(appDir, j); // 비켜 둔 파일(Stale)도 일지에 — 다음 시작 때 마저 되돌리고 지운다
            throw new ToolException($"새 파일을 복사하지 못했습니다. 아무것도 바꾸지 않았습니다: {ex.Message}");
        }

        // b. 이름 바꾸기
        try
        {
            foreach (string rel in items)
            {
                string target = Path.Combine(appDir, rel);
                if (File.Exists(target)) Move(target, target + ".old"); // 실행 중인 exe 도 이름은 바꿀 수 있다
                Move(target + ".new", target);
                Step?.Invoke("swap:" + rel);
            }
            j.State = Applied;
            Save(appDir, j);
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            bool clean = Undo(appDir, j);
            if (clean) Close(appDir, j.Stale);
            else TrySave(appDir, j);
            throw new ToolException(clean ? $"새 파일로 바꾸지 못해 원래대로 되돌렸습니다: {ex.Message}"
                                          : $"새 파일로 바꾸지 못했고 일부는 되돌리지 못했습니다. 프로그램을 다시 시작하면 마저 되돌립니다: {ex.Message}");
        }
    }

    // 받아 둔 업데이트 묶음 지우기 (프로그램이 시작할 때 — 그때는 진행 중인 업데이트가 없다)
    public static void CleanupDownloads()
    {
        Files.DeleteDirectoryQuietly(DownloadsDir);
    }

    // 바꿔 넣은 뒤 새 버전을 실행하지 못했을 때: 일지에 적힌 대로 원래 파일로 되돌린다 (새로 생긴 파일은 지움)
    public enum RevertResult { Reverted, Partial, NothingToRevert }

    // Reverted: 다 되돌림, Partial: 일부만 (다음 시작 때 마저 — FinishInterrupted), NothingToRevert: 되돌릴 업데이트가 없음(일지 없음)
    public static RevertResult Revert(string appDir)
    {
        if (Load(appDir) is not { } j) return RevertResult.NothingToRevert;
        if (Undo(appDir, j))
        {
            Close(appDir, j.Stale);
            return RevertResult.Reverted;
        }
        // 못 되돌린 것은 다음 시작 때 마저 (끝난 업데이트로 보고 .old 를 지우지 않게). 비켜 둔 파일(Stale)도 같이 적는다
        if (j.State == Applied) j.State = Swapping;
        TrySave(appDir, j);
        return RevertResult.Partial;
    }

    // 프로그램이 시작할 때, 창을 띄우기 전에: 도중에 멈추거나 일부만 되돌린 업데이트(일지가 끝남이 아님)를 마저 정리한다
    // — 옛 파일과 새 파일이 섞인 채로는 창을 만들다 실패할 수 있어 CleanupOld(창이 뜬 뒤)까지 기다리지 않는다.
    // 끝난 업데이트(.old 정리만 남음)는 건드리지 않는다 (새 버전의 창이 뜬 뒤에 정리 — 뜨지 않으면 그것으로 되돌리므로).
    public static void FinishInterrupted(string appDir)
    {
        if (Load(appDir) is { } j && j.State != Applied) Settle(appDir);
    }

    private static void TrySave(string appDir, Journal j)
    {
        try { Save(appDir, j); } catch (Exception ex) when (Errors.IsFileError(ex)) { }
    }

    // 프로그램이 시작할 때: 지난 업데이트가 비켜 둔 .old 를 지우고, 도중에 멈춘 업데이트는 되돌린다. 아직 쓰이는 것은 다음에.
    // 일지에 적힌 파일만 — 사용자의 다른 *.old 는 건드리지 않는다.
    public static void CleanupOld(string appDir) => Settle(appDir);

    // 지난 업데이트의 뒷정리. 못 지운 .old 목록을 돌려준다 (일지에 남김). 도중에 멈춘 것을 되돌리지 못했으면 null (일지는 그대로).
    private static List<string>? Settle(string appDir)
    {
        if (Load(appDir) is not { } j) return [];
        // 이름 바꾸기를 다 하고 "끝남"만 못 적고 멈췄으면 끝난 업데이트로 본다 — 지금 실행 중인 것이 새 GUI exe 라 되돌리면
        // 그 exe 만 못 지우고 나머지만 옛 것으로 돌아가 섞인다. 되돌리다 멈춘 것(Revert 도 Swapping 으로 남긴다)과는 비켜 둔
        // .old 로 가른다: 다 바꿨으면 원래 파일마다 .old 가 있고, 되돌린 파일은 .old 가 제자리로 돌아가 없다.
        if (j.State == Swapping && j.Items.Count > 0 &&
            j.Items.All(rel => Full(appDir, rel) is { } f && File.Exists(f) && !File.Exists(f + ".new")) &&
            j.Replaced.All(rel => Full(appDir, rel) is { } f && File.Exists(f + ".old")))
            j.State = Applied;
        if (j.State == Applied) return Close(appDir, [.. j.Replaced.Select(rel => rel + ".old"), .. j.Stale]);
        return Undo(appDir, j) ? Close(appDir, j.Stale) : null;
    }

    // .old 들을 지우고 일지를 닫는다. 못 지운 것은 일지에 남겨 다음에 다시.
    private static List<string> Close(string appDir, IEnumerable<string> olds)
    {
        var left = olds.Distinct(StringComparer.OrdinalIgnoreCase).Where(rel => Full(appDir, rel) is { } f && !Files.TryDeleteFile(f)).ToList();
        try
        {
            if (left.Count == 0) File.Delete(Path.Combine(appDir, JournalFile));
            else Save(appDir, new Journal { State = Applied, Stale = left });
        }
        catch (Exception ex) when (Errors.IsFileError(ex)) { }
        return left;
    }

    // 일지대로 되돌린다: 비켜 둔 원래 파일을 제자리로, 새로 생긴 파일과 .new 는 지움. 몇 번을 해도 같다. 다 되면 true.
    private static bool Undo(string appDir, Journal j)
    {
        bool ok = true;
        foreach (string rel in Enumerable.Reverse(j.Items))
        {
            if (Full(appDir, rel) is not { } target) continue;
            string old = target + ".old", fresh = target + ".new";
            try
            {
                if (j.Replaced.Contains(rel) && File.Exists(old)) // .old 는 복사 단계에서 비웠으므로 있으면 우리가 비켜 둔 것
                {
                    if (File.Exists(target)) MoveAside(appDir, j, rel, target);
                    Move(old, target);
                }
                else if (j.Created.Contains(rel) && !File.Exists(fresh) && File.Exists(target)) File.Delete(target); // .new 가 없으면 이미 제자리로 옮긴 것
                if (File.Exists(fresh)) File.Delete(fresh);
            }
            catch (Exception ex) when (Errors.IsFileError(ex)) { ok = false; }
        }
        return ok;
    }

    // 이름 바꾸기. 방금 복사한 파일은 백신이 검사하느라 잠깐 잡고 있을 수 있어(공유 위반) 잠시 뒤 몇 번 더 해 본다.
    // 그래도 안 되면 어느 파일인지 붙여 알린다.
    private static void Move(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < MoveAttempts && File.Exists(from) && !File.Exists(to))
            {
                Thread.Sleep(500);
            }
            catch (Exception ex) when (Errors.IsFileError(ex))
            {
                throw new IOException($"{Path.GetFileName(from)} -> {Path.GetFileName(to)}: {ex.Message}", ex);
            }
        }
    }

    private const int MoveAttempts = 10; // 약 5초

    // 새 파일을 지우지 않고 다른 이름으로 비켜 둔다 — 실행 중인 exe(되돌리는 쪽이 새 버전 자신일 때)는 지울 수 없지만 이름은
    // 바꿀 수 있다. 바로 지워 보고, 못 지우면 일지의 Stale 에 남겨 다음 시작 때 지운다.
    private static void MoveAside(string appDir, Journal j, string rel, string target)
    {
        string suffix = $".discard-{Guid.NewGuid():N}.old"; // 일지의 지울 목록은 .old 만 받는다 (Load)
        Move(target, target + suffix);
        if (!Files.TryDeleteFile(target + suffix)) j.Stale.Add(rel + suffix);
    }

    // 바꿔 넣기 일지. 없거나 읽지 못하면 null (진행 중인 업데이트가 없는 것으로)
    private static Journal? Load(string appDir)
    {
        string path = Path.Combine(appDir, JournalFile);
        try
        {
            if (!File.Exists(path)) return null;
            var j = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path));
            if (j == null) return null;
            j.Items ??= []; j.Replaced ??= []; j.Created ??= []; j.Stale ??= [];
            j.Stale.RemoveAll(rel => !rel.EndsWith(".old", StringComparison.OrdinalIgnoreCase)); // 지우기만 하는 목록이라 .old 만
            return j;
        }
        catch (Exception ex) when (ex is JsonException || Errors.IsFileError(ex)) { return null; }
    }

    private static void Save(string appDir, Journal j) =>
        Files.WriteAtomic(Path.Combine(appDir, JournalFile), JsonSerializer.Serialize(j, JsonOptions.Indented));

    // 일지의 상대 경로 -> 프로그램 폴더 안의 전체 경로 (밖을 가리키면 null — 손댄 일지로 엉뚱한 파일을 건드리지 않게)
    private static string? Full(string appDir, string rel)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir)) + Path.DirectorySeparatorChar;
        if (string.IsNullOrEmpty(rel) || Path.IsPathRooted(rel)) return null;
        string full = Path.GetFullPath(Path.Combine(root, rel));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string? ReadText(string source) =>
        source.StartsWith("https://", StringComparison.Ordinal) ? Http.GetStringOrNull(source)
        : File.Exists(source) ? File.ReadAllText(source) : null;

    // 받다 끊기면(연결 끊김, 60초 무응답) 처음부터 한 번 더 받는다 (다 받은 뒤 SHA256 과 서명으로 확인하므로 이어 붙이지 않음)
    private static void Fetch(string source, string dest, Action<double>? progress)
    {
        if (!source.StartsWith("https://", StringComparison.Ordinal))
        {
            File.Copy(source, dest);
            progress?.Invoke(1);
            return;
        }
        for (int attempt = 1; ; attempt++)
        {
            using var resp = Http.Send(source) ?? throw Http.NotFound(source);
            long? total = resp.Content.Headers.ContentLength;
            using var body = Http.OpenBody(resp);
            using var file = File.Create(dest);
            var buffer = new byte[81920];
            long read = 0;
            Exception cut;
            while (true)
            {
                int n;
                // 받는 쪽 실패만 다시 한다 (파일 쓰기 실패는 그대로 올린다)
                try { n = body.Read(buffer, 0, buffer.Length); }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { cut = ex; break; }
                if (n <= 0) return;
                file.Write(buffer, 0, n);
                read += n;
                if (total > 0) progress?.Invoke((double)read / total.Value);
            }
            if (attempt >= 2) throw Http.BodyFailed(source, cut);
            Http.Wait(TimeSpan.FromSeconds(3) * Http.BackoffScale, source, $"받는 도중 끊김, 처음부터 ({cut.Message})");
            progress?.Invoke(0);
        }
    }
}
