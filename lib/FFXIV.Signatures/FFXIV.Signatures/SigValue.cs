namespace FFXIV.Signatures;

// 시그니처가 가리키는 값 — OverlayPlugin / cactbot SigScan 의 규칙. 일치 시작 A, 일치 길이 L, offset o (음수 가능):
//   rip = true   v = int32 at [A+L+o],  결과 = A+L+o+4+ripOffset+v   (RIP 상대 변위 / rel32)
//   rip = false  결과 = int64 at [A+L+o] - ImageBase                  (절대 주소. exe 파일의 값은 기본 ImageBase 기준)
// 프로세스 메모리 기준 규칙을 RVA 로 그대로 계산한다 (상대 값은 적재 주소와 상관없다).
// ReadRva: 값을 읽은 자리, ResolvedRva: 결과 RVA, Section: 결과가 속한 섹션 (어디에도 없으면 null — 엉뚱한 곳일 가능성),
// Error: 읽지 못했거나 결과를 RVA 로 나타낼 수 없을 때 까닭 (그때 ResolvedRva 는 null).
public sealed record SigValue(int ReadRva, int? ResolvedRva, string? Section, string? Error = null)
{
    public static SigValue Resolve(PeImage image, SigPattern.Match match, int offset, bool rip, int ripOffset = 0)
    {
        long read = (long)match.Rva + match.Length + offset;
        if (read is < 0 or > int.MaxValue) return new SigValue(0, null, null, $"읽을 자리가 이미지 밖 ({read})");
        int at = (int)read;
        long resolved;
        if (rip)
        {
            if (image.ReadInt32(at) is not { } v) return new SigValue(at, null, null, $"0x{at:x} 의 4바이트가 파일에 없음");
            resolved = at + 4L + ripOffset + v;
        }
        else
        {
            if (image.ReadInt64(at) is not { } v) return new SigValue(at, null, null, $"0x{at:x} 의 8바이트가 파일에 없음");
            if (image.ImageBase == 0) return new SigValue(at, null, null, "ImageBase 를 모름 (선택 헤더 없음)");
            ulong value = unchecked((ulong)v);
            if (value < image.ImageBase || value - image.ImageBase > int.MaxValue)
                return new SigValue(at, null, null, $"절대 주소 0x{value:x} 가 이미지 밖 (ImageBase 0x{image.ImageBase:x})");
            resolved = (long)(value - image.ImageBase);
        }
        if (resolved is < 0 or > int.MaxValue) return new SigValue(at, null, null, $"결과 주소가 이미지 밖 ({resolved})");
        return new SigValue(at, (int)resolved, image.SectionAt(resolved)?.Name);
    }
}
