// 저장소 최신 스크립트로 만든 결과를 작업 폴더에 받아 두고 다시 쓰는 흐름 (IDA, 로컬 opcodediff 가 같이 쓴다).
//   1) 스크립트를 받는다 — 받지 못하면 받아 둔 결과
//   2) 스크립트 해시를 그 결과를 만들 때 남긴 stamp(scripts.sha256)와 비교 — 같으면 받아 둔 결과
//   3) 다르면 옆의 <작업 폴더>.new 에서 새로 돌리고(NewDir), 끝까지 성공해야 작업 폴더와 바꾼다(Commit) — 실패하면 받아 둔 결과
// overwrite 면 받아 둔 결과를 쓰지 않는다.
internal static class ScriptCache
{
    public const string StampFile = "scripts.sha256"; // 결과를 만든 스크립트의 해시

    // 받아 둔 결과를 쓸 때 남길 말 (FetchFailed: 받지 못한 까닭, RunFailed: 다시 돌리지 못한 까닭을 받는다)
    public sealed record Notes(Func<string, string> FetchFailed, string Cached, string Changed, Func<string, string> RunFailed);

    // workDir: 작업 폴더(stamp 가 있는 곳), cache: 받아 둔 결과 파일.
    // trustUnstamped: stamp 가 없는 결과(해시를 남기기 전에 만든 것)를 지금 스크립트로 만든 것으로 볼지 — true 면 지금 해시를
    // 써 넣고 그 결과를 쓰고, false 면 다시 돌린다.
    // fresh: 받은 스크립트와 그 해시로 새로 돌린다.
    public static T Run<TScripts, T>(string workDir, string cache, bool overwrite, bool trustUnstamped,
                                     Func<TScripts> fetch, Func<TScripts, string> hash, Func<T> readCache,
                                     Func<TScripts, string, T> fresh, Notes notes)
    {
        string stamp = Path.Combine(workDir, StampFile);
        bool fallback = !overwrite && File.Exists(cache); // 다시 돌리다 실패하면 받아 둔 결과를 쓴다
        TScripts scripts;
        try { scripts = fetch(); }
        catch (Exception ex) when (fallback && Errors.Recoverable(ex))
        {
            Log.Note(notes.FetchFailed(Errors.Describe(ex)));
            return readCache();
        }
        string scriptHash = hash(scripts);
        if (fallback)
        {
            if (!File.Exists(stamp) && trustUnstamped)
            {
                try { Files.WriteAtomic(stamp, scriptHash); } catch (Exception ex) when (Errors.IsFileError(ex)) { }
                Log.Note(notes.Cached);
                return readCache();
            }
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == scriptHash)
            {
                Log.Note(notes.Cached);
                return readCache();
            }
            Log.Note(notes.Changed);
        }
        try { return fresh(scripts, scriptHash); }
        catch (Exception ex) when (fallback && Errors.Recoverable(ex))
        {
            Log.Warn(notes.RunFailed(ex.Message));
            return readCache();
        }
    }

    // 새로 돌릴 빈 폴더 <dest>.new (지난번에 남은 것은 지운다)
    public static string NewDir(string dest)
    {
        string dir = dest + ".new";
        Files.ResetDirectory(dir);
        return dir;
    }

    // 다 됐으면 받아 둔 결과와 바꾼다 (바꾸지 못하면 이번 결과만 쓰고, 받아 둔 것은 그대로 — 다시 돌리지 않게).
    // 쓸 결과 파일(fileName)의 경로를 돌려준다. what: 로그에 쓸 이름
    public static string Commit(string fresh, string dest, string fileName, string what)
    {
        try
        {
            Files.ReplaceDirectory(fresh, dest);
            return Path.Combine(dest, fileName);
        }
        catch (Exception ex) when (Errors.IsFileError(ex))
        {
            Log.Note($"[opcode] {what} 결과 폴더를 바꾸지 못해 이번 결과만 씀 ({ex.Message})");
            return Path.Combine(fresh, fileName);
        }
    }
}
