using System.Text;

namespace ActPluginRepack.Tests;

// 통째로 바꿔 쓰기 (Files.WriteAtomic) 와 그걸 쓰는 시그니처 입력 파일
public class FilesTests
{
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
