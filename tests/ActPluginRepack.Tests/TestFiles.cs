using System.Text;

namespace ActPluginRepack.Tests;

// 시험마다 쓰고 지우는 임시 폴더
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ActPluginRepack.Tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, string content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    public string File(string relative, byte[] content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public string Dir(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class TestEnv
{
    // ACT 나 게임이 떠 있으면 설치가 (의도대로) 거부되므로 건너뛴다
    public static void SkipIfActOrGameRunning()
    {
        foreach (string name in new[] { "Advanced Combat Tracker", "ffxiv_dx11" })
        {
            var ps = System.Diagnostics.Process.GetProcessesByName(name);
            foreach (var p in ps) p.Dispose();
            if (ps.Length > 0) Assert.Skip($"{name} 실행 중");
        }
    }
}

// .text 섹션 하나만 있는 최소 PE (시그니처 검색 시험용). .text 는 RVA 0x1000, 파일 오프셋 0x200.
internal static class FakePe
{
    public const int TextRva = 0x1000;

    public static byte[] Build(byte[] text)
    {
        const int pe = 0x40, raw = 0x200;
        var b = new byte[raw + text.Length];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(pe).CopyTo(b, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(b, pe);
        BitConverter.GetBytes((ushort)1).CopyTo(b, pe + 6);   // NumberOfSections
        BitConverter.GetBytes((ushort)0).CopyTo(b, pe + 20);  // SizeOfOptionalHeader
        int s = pe + 24;
        Encoding.ASCII.GetBytes(".text").CopyTo(b, s);
        BitConverter.GetBytes(text.Length).CopyTo(b, s + 8);  // VirtualSize
        BitConverter.GetBytes(TextRva).CopyTo(b, s + 12);     // VirtualAddress
        BitConverter.GetBytes(text.Length).CopyTo(b, s + 16); // SizeOfRawData
        BitConverter.GetBytes(raw).CopyTo(b, s + 20);         // PointerToRawData
        text.CopyTo(b, raw);
        return b;
    }
}
