namespace FFXIV.Signatures;

// PE 파일(ffxiv_dx11.exe / deucalion dll)의 섹션 헤더만 읽는 최소 리더.
// 시그니처 스캔, 값 해석(RVA <-> 파일 위치), deucalion 문자열 패치(.rdata/.rustc)에 쓴다.
public sealed class PeImage
{
    // Characteristics: IMAGE_SCN_* (실행 가능 = 0x20000000)
    public sealed record Section(string Name, int VirtualAddress, int VirtualSize, int RawOffset, int RawSize, uint Characteristics = 0)
    {
        public bool Executable => (Characteristics & 0x20000000) != 0;

        // 메모리에 올라갔을 때의 크기 (VirtualSize 가 0 인 오래된 링커 출력은 RawSize)
        public int MappedSize => VirtualSize > 0 ? VirtualSize : RawSize;

        // 파일에 실제로 있는 크기 (파일에 없는 가상 꼬리 — .data 의 bss 등 — 는 제외)
        public int FileSize => VirtualSize > 0 ? Math.Min(VirtualSize, RawSize) : RawSize;
    }

    public byte[] Bytes { get; }
    public IReadOnlyList<Section> Sections { get; }

    // 선택 헤더의 ImageBase (기본 적재 주소). exe 안의 절대 주소는 이 값 기준이다. 선택 헤더가 없으면 0.
    public ulong ImageBase { get; }

    private PeImage(byte[] bytes, List<Section> sections, ulong imageBase)
    {
        Bytes = bytes;
        Sections = sections;
        ImageBase = imageBase;
    }

    // 파일을 읽지 못하면(없음, 권한, 잘못된 경로) SignatureException. path 가 null 이면 ArgumentNullException.
    public static PeImage Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new SignatureException($"파일을 읽지 못했습니다: {path} ({ex.Message})");
        }
        return Parse(bytes, path);
    }

    // what: 오류 문구에 넣을 이름 (경로 등). d 가 null 이면 ArgumentNullException.
    public static PeImage Parse(byte[] d, string what)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') throw new SignatureException($"PE 파일이 아닙니다: {what}");
        int pe = BitConverter.ToInt32(d, 0x3C);
        if (pe <= 0 || (long)pe + 24 > d.Length || BitConverter.ToUInt32(d, pe) != 0x4550)
            throw new SignatureException($"PE 파일이 아닙니다: {what}");
        int count = BitConverter.ToUInt16(d, pe + 6);
        int optSize = BitConverter.ToUInt16(d, pe + 20);
        var sections = new List<Section>(count);
        if ((long)pe + 24 + optSize + count * 40 > d.Length) throw new SignatureException($"PE 파일이 잘렸습니다: {what}");
        for (int i = 0, o = pe + 24 + optSize; i < count; i++, o += 40)
        {
            string name = System.Text.Encoding.ASCII.GetString(d, o, 8).TrimEnd('\0');
            sections.Add(new Section(name,
                VirtualAddress: BitConverter.ToInt32(d, o + 12),
                VirtualSize: BitConverter.ToInt32(d, o + 8),
                RawOffset: BitConverter.ToInt32(d, o + 20),
                RawSize: BitConverter.ToInt32(d, o + 16),
                Characteristics: BitConverter.ToUInt32(d, o + 36)));
        }
        // 파일 데이터가 없는 섹션(RawSize 0 — bss 등)의 PointerToRawData 는 로더도 보지 않으므로 검사하지 않는다
        if (sections.Any(s => s.RawSize < 0 || (s.RawSize > 0 && (s.RawOffset < 0 || s.RawOffset > d.Length))))
            throw new SignatureException($"PE 파일이 잘렸거나 깨졌습니다: {what}");
        return new PeImage(d, sections, ReadImageBase(d, pe + 24, optSize));
    }

    // PE32+ (0x20B): +24 에 8바이트, PE32 (0x10B): +28 에 4바이트
    private static ulong ReadImageBase(byte[] d, int opt, int optSize)
    {
        if (optSize < 2) return 0;
        return BitConverter.ToUInt16(d, opt) switch
        {
            0x20B when optSize >= 32 => BitConverter.ToUInt64(d, opt + 24),
            0x10B when optSize >= 32 => BitConverter.ToUInt32(d, opt + 28),
            _ => 0,
        };
    }

    public Section? Find(string name) => Sections.FirstOrDefault(s => s.Name == name);

    public Section Require(string name) =>
        Find(name) ?? throw new SignatureException($"'{name}' PE 섹션이 없습니다.");

    // 섹션의 파일 데이터 (파일 정렬로 붙은 꼬리 채움 포함. 잘린 파일이면 있는 데까지)
    public ReadOnlySpan<byte> Span(Section s) => Slice(s, s.RawSize);

    // 섹션 중 메모리에도 올라가는 부분 (파일 정렬로 붙은 꼬리 채움은 뺀다)
    public ReadOnlySpan<byte> FileSpan(Section s) => Slice(s, s.FileSize);

    private ReadOnlySpan<byte> Slice(Section s, int size)
    {
        if (size <= 0 || s.RawOffset < 0 || s.RawOffset >= Bytes.Length) return [];
        return Bytes.AsSpan(s.RawOffset, Math.Min(size, Bytes.Length - s.RawOffset));
    }

    // rva 가 속한 섹션 (메모리에 올라간 크기 기준이라 .data 의 bss 꼬리도 .data). 어디에도 없으면 null.
    public Section? SectionAt(long rva) =>
        Sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < (long)s.VirtualAddress + s.MappedSize);

    // rva 의 파일 위치. 파일에 없는 자리(섹션 밖, 가상 꼬리)면 null.
    public int? FileOffset(long rva)
    {
        foreach (var s in Sections)
            if (rva >= s.VirtualAddress && rva < (long)s.VirtualAddress + s.FileSize)
            {
                long off = s.RawOffset + (rva - s.VirtualAddress);
                return off < Bytes.Length ? (int)off : null; // 잘린 파일: 헤더는 있다고 하지만 데이터가 없음
            }
        return null;
    }

    // rva 에서 size 바이트가 모두 파일에 있으면 그 파일 위치, 아니면 null
    private int? Readable(long rva, int size)
    {
        if (FileOffset(rva) is not { } off || FileOffset(rva + size - 1) != off + size - 1) return null;
        return off + size <= Bytes.Length ? off : null;
    }

    public int? ReadInt32(long rva) => Readable(rva, 4) is { } off ? BitConverter.ToInt32(Bytes, off) : null;

    public long? ReadInt64(long rva) => Readable(rva, 8) is { } off ? BitConverter.ToInt64(Bytes, off) : null;
}
