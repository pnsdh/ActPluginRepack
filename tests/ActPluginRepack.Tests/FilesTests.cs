using System.Text;

namespace ActPluginRepack.Tests;

// 통째로 바꿔 쓰기 (Files.WriteAtomic) 와 그걸 쓰는 시그니처 입력 파일
public class FilesTests
{
    // 옆에 다 만든 폴더로 바꾸기: 있던 폴더는 새 것으로, 비켜 둔 것은 남지 않는다
    [Fact]
    public void ReplaceDirectorySwapsInTheFreshFolder()
    {
        using var tmp = new TempDir();
        string dest = tmp.Dir("result"), fresh = tmp.Dir("result.new");
        tmp.File("result/diff.json", "old");
        tmp.File("result.new/diff.json", "new");
        Files.ReplaceDirectory(fresh, dest);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "diff.json")));
        Assert.False(Directory.Exists(fresh));
        Assert.False(Directory.Exists(dest + ".old"));

        // 바꿀 대상이 없어도
        string fresh2 = tmp.Dir("other.new");
        tmp.File("other.new/x", "1");
        Files.ReplaceDirectory(fresh2, Path.Combine(tmp.Path, "other"));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "other", "x")));

        // 지난번에 못 지운 비켜 둔 폴더(이름이 매번 다름)는 다음에 바꿀 때 지운다
        tmp.File("result.old-0123/diff.json", "older");
        string fresh3 = tmp.Dir("result.new");
        tmp.File("result.new/diff.json", "newer");
        Files.ReplaceDirectory(fresh3, dest);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(dest, "diff.json")));
        Assert.Empty(Directory.GetDirectories(tmp.Path, "result.old*"));
    }

    // 쓰다 죽어 남은 임시 파일(이름이 매번 달라 덮이지 않음)은 다음에 쓸 때 정리한다 — 지금 다른 곳이 쓰는 중일 수 있는 새 것은 둔다
    [Fact]
    public void WriteAtomicRemovesStaleTempFiles()
    {
        using var tmp = new TempDir();
        string target = Path.Combine(tmp.Path, "config.json");
        string stale = tmp.File("config.json.1234.abcd.tmp", "half");
        string legacy = tmp.File("config.json.tmp", "half");
        string fresh = tmp.File("config.json.5678.efgh.tmp", "writing");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddHours(-2));
        Files.WriteAtomic(target, "{}");
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(fresh));
        Assert.Equal("{}", File.ReadAllText(target));
    }

    [Fact]
    public void WriteAtomicCreatesFolderAndReplaces()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "새 폴더", "a.txt");
        Files.WriteAtomic(path, "하나");
        Files.WriteAtomic(path, "둘");
        Assert.Equal("둘"u8.ToArray(), File.ReadAllBytes(path)); // BOM 없음
        Assert.False(File.Exists(path + ".tmp"));

        Files.WriteAtomic(path, [1, 2, 3]);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Files.WriteAtomic(path, "x", Encoding.UTF8);
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'x'], File.ReadAllBytes(path));
    }

    [Fact]
    public void SignatureUpsertKeepsFileFormat()
    {
        using var tmp = new TempDir();
        string path = tmp.File("deucalion.txt", "# 내가 쓴 줄\r\nRecv|E8 ?? ?? ?? ??\r\n");
        SignatureInput.Upsert(tmp.Path, "deucalion.txt", "Send", "48 8B", "test");
        Assert.Equal("# 내가 쓴 줄\r\nRecv|E8 ?? ?? ?? ??\r\n# auto: test\r\nSend|48 8B\r\n".Replace("\r\n", Environment.NewLine),
                     File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        var (removed, manual) = SignatureInput.PruneAuto(tmp.Path, "deucalion.txt", _ => true);
        Assert.Equal(["Send"], removed);
        Assert.Equal(["Recv"], manual);
        Assert.Equal("# 내가 쓴 줄\r\nRecv|E8 ?? ?? ?? ??\r\n".Replace("\r\n", Environment.NewLine), File.ReadAllText(path));
    }
}
