using System.Text;

namespace FFXIV.Signatures.Tests;

// 시험용 최소 PE32+ (선택 헤더는 magic 과 ImageBase 만 채움). 섹션은 RVA 0x1000 부터 0x1000 간격, 파일에는 0x200 정렬로 차례로.
internal static class FakePe
{
    public const ulong ImageBase = 0x140000000;
    public const int TextRva = 0x1000;
    public const uint Code = 0x60000020, Data = 0xC0000040; // 실행+읽기 코드 / 읽기+쓰기 데이터

    public static byte[] Text(byte[] text) => Build((".text", text, Code, 0));

    // virtualExtra: 파일 데이터(정렬된 RawSize) 뒤에 붙는, 파일에 없는 가상 꼬리 (bss)
    public static byte[] Build(params (string Name, byte[] Data, uint Characteristics, int VirtualExtra)[] sections)
    {
        const int pe = 0x40, optSize = 0xF0, align = 0x200;
        int raw = align;
        var offsets = new List<int>();
        foreach (var s in sections)
        {
            offsets.Add(raw);
            raw += (s.Data.Length + align - 1) / align * align;
        }
        var b = new byte[raw];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(pe).CopyTo(b, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(b, pe);
        BitConverter.GetBytes((ushort)sections.Length).CopyTo(b, pe + 6);
        BitConverter.GetBytes((ushort)optSize).CopyTo(b, pe + 20);
        int opt = pe + 24;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(b, opt);
        BitConverter.GetBytes(ImageBase).CopyTo(b, opt + 24);
        for (int i = 0; i < sections.Length; i++)
        {
            int h = opt + optSize + i * 40;
            var (name, data, characteristics, extra) = sections[i];
            Encoding.ASCII.GetBytes(name).CopyTo(b, h);
            int rawSize = (data.Length + align - 1) / align * align;
            BitConverter.GetBytes(extra > 0 ? rawSize + extra : data.Length).CopyTo(b, h + 8); // VirtualSize
            BitConverter.GetBytes(TextRva * (i + 1)).CopyTo(b, h + 12);    // VirtualAddress
            BitConverter.GetBytes(rawSize).CopyTo(b, h + 16);              // SizeOfRawData (정렬)
            BitConverter.GetBytes(offsets[i]).CopyTo(b, h + 20);           // PointerToRawData
            BitConverter.GetBytes(characteristics).CopyTo(b, h + 36);
            data.CopyTo(b, offsets[i]);
        }
        return b;
    }

    // 함수 하나: 코드와 고칠 rel32 자리. Rip: (함수 안 자리, .data 안 offset), Calls: (함수 안 자리, 대상 함수 번호).
    // rel32 는 그 자리 + 4 기준 (RIP 변위 / call 의 rel32 가 명령어 끝에 있을 때)
    public sealed record Fn(byte[] Code, (int At, int Data)[]? Rip = null, (int At, int Target)[]? Calls = null);

    public const int DataRva = 0x2000;

    // 함수들을 .text 에 0x40 정렬로 차례로 놓고 .pdata 를 만든 exe (.text / .data 0x100 / .pdata). 함수 시작 RVA 도 돌려준다.
    public static (byte[] Pe, int[] Starts) Functions(params Fn[] fns)
    {
        var starts = new int[fns.Length];
        int at = 0;
        for (int i = 0; i < fns.Length; i++)
        {
            starts[i] = TextRva + at;
            at = (at + fns[i].Code.Length + 0x3F) & ~0x3F;
        }
        var text = Enumerable.Repeat((byte)0xCC, at).ToArray();
        var pdata = new List<byte>();
        for (int i = 0; i < fns.Length; i++)
        {
            var code = (byte[])fns[i].Code.Clone();
            foreach (var (pos, data) in fns[i].Rip ?? [])
                BitConverter.GetBytes(DataRva + data - (starts[i] + pos + 4)).CopyTo(code, pos);
            foreach (var (pos, target) in fns[i].Calls ?? [])
                BitConverter.GetBytes(starts[target] - (starts[i] + pos + 4)).CopyTo(code, pos);
            code.CopyTo(text, starts[i] - TextRva);
            pdata.AddRange(BitConverter.GetBytes(starts[i]));
            pdata.AddRange(BitConverter.GetBytes(starts[i] + code.Length));
            pdata.AddRange(new byte[4]);
        }
        pdata.AddRange(new byte[12]);
        if (text.Length > 0x1000) throw new ArgumentException("시험용 .text 는 0x1000 바이트까지");
        return (Build((".text", text, Code, 0), (".data", new byte[0x100], Data, 0), (".pdata", [.. pdata], 0x40000040, 0)), starts);
    }

    public static PeImage Image(byte[] pe) => PeImage.Parse(pe, "test");

    public static ScanTarget Target(byte[] pe, ScanScope scope = ScanScope.Text) => new(Image(pe), "test", scope);
}
