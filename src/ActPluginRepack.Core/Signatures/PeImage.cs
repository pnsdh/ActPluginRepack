// PE 파일(ffxiv_dx11.exe / deucalion dll)의 섹션 헤더만 읽는 최소 리더.
// 시그니처 스캔(.text)과 deucalion 문자열 패치(.rdata/.rustc)에 쓴다.
internal sealed class PeImage
{
    public sealed record Section(string Name, int VirtualAddress, int VirtualSize, int RawOffset, int RawSize);

    public byte[] Bytes { get; }
    public IReadOnlyList<Section> Sections { get; }

    private PeImage(byte[] bytes, List<Section> sections)
    {
        Bytes = bytes;
        Sections = sections;
    }

    public static PeImage Load(string path) => Parse(File.ReadAllBytes(path), path);

    public static PeImage Parse(byte[] d, string what)
    {
        if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') throw new ToolException($"PE 파일이 아닙니다: {what}");
        int pe = BitConverter.ToInt32(d, 0x3C);
        if (pe <= 0 || pe + 24 > d.Length || BitConverter.ToUInt32(d, pe) != 0x4550)
            throw new ToolException($"PE 파일이 아닙니다: {what}");
        int count = BitConverter.ToUInt16(d, pe + 6);
        int optSize = BitConverter.ToUInt16(d, pe + 20);
        var sections = new List<Section>(count);
        if (pe + 24 + optSize + count * 40 > d.Length) throw new ToolException($"PE 파일이 잘렸습니다: {what}");
        for (int i = 0, o = pe + 24 + optSize; i < count; i++, o += 40)
        {
            string name = System.Text.Encoding.ASCII.GetString(d, o, 8).TrimEnd('\0');
            sections.Add(new Section(name,
                VirtualAddress: BitConverter.ToInt32(d, o + 12),
                VirtualSize: BitConverter.ToInt32(d, o + 8),
                RawOffset: BitConverter.ToInt32(d, o + 20),
                RawSize: BitConverter.ToInt32(d, o + 16)));
        }
        if (sections.Any(s => s.RawOffset < 0 || s.RawSize < 0 || s.RawOffset > d.Length))
            throw new ToolException($"PE 파일이 잘렸거나 깨졌습니다: {what}");
        return new PeImage(d, sections);
    }

    public Section? Find(string name) => Sections.FirstOrDefault(s => s.Name == name);

    public Section Require(string name) =>
        Find(name) ?? throw new ToolException($"'{name}' PE 섹션이 없습니다.");

    public ReadOnlySpan<byte> Span(Section s) =>
        Bytes.AsSpan(s.RawOffset, Math.Min(s.RawSize, Bytes.Length - s.RawOffset));
}
