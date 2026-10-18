using System.Text.RegularExpressions;

// FFXIVClientStructs 의 ida/data.yml — 글로벌 exe 의 전역 변수·클래스 인스턴스·vtable 주소에 이름을 붙인 자료(사람이 패치마다
// 갱신, 7.57 은 출시 약 1시간 뒤). 자동 복구가 만든 메모리 시그니처를 독립 자료로 검문하는 데만 쓴다 (SignatureRepair):
// 원래 시그니처가 옛 exe 에서 읽던 값과 새 시그니처가 새 exe 에서 읽는 값을 이름으로 맞대 본다 (Compare).
//   - 지금 판은 main 의 ida/data.yml (머리의 version 이 그 게임 버전일 때만), 지난 판은 ida/old/data_<버전>.yml.
//   - 데이터 쪽 기호만 쓴다 (globals, classes 의 instances / vtbls). 함수 이름은 쓰지 않는다 — 패킷 후크는 이 자료에 이름이 없고,
//     이름 없는 함수는 "가까운 이름 + 거리"가 패치마다 바뀌어 대조할 수 없다.
//   - 자료에도 오류·누락이 있고 판마다 이름이 생기고 사라진다(같은 주소의 global 이 다음 판에서 #fail 로 빠지는 등) — 그래서 두 판에
//     모두 있는 이름만 쓰고, 버리는 것은 두 값이 모두 이름 붙은 주소 그 자체인데 이름이 다를 때뿐이다 ("가까운 이름 + 거리"로는
//     확인 표시만 하고 버리지 않는다 — 이름 없는 객체가 옆 이름에 붙은 것일 수 있어, 배치가 바뀌면 옳은 후보도 다르게 보인다).
// 주소가 글로벌 exe 기준이라 다른 지역 클라이언트는 대조하지 않는다.
internal static class ClientStructsData
{
    private const string Raw = "https://raw.githubusercontent.com/aers/FFXIVClientStructs/main/ida";
    private const long MaxFieldOffset = 0x1000; // 기호 뒤 이 거리 안이면 그 객체의 필드로 본다 (그 밖은 모름)

    public enum Verdict { Unknown, Confirmed, Mismatch }

    // VA -> 데이터 기호 이름 (정렬)
    public sealed class Symbols(SortedList<long, string> map)
    {
        public int Count => map.Count;

        // 두 판에 모두 있는 이름만 남긴 것 (한쪽 판에만 있는 이름 때문에 같은 자리가 다른 이름으로 잡히지 않게)
        public Symbols Common(Symbols other)
        {
            var names = other.Names();
            var kept = new SortedList<long, string>();
            foreach (var (va, name) in map) if (names.Contains(name)) kept.Add(va, name);
            return new Symbols(kept);
        }

        private HashSet<string> Names() => [.. map.Values];

        // va 를 담은 기호와 거리 (바로 앞 기호에서 MaxFieldOffset 안일 때만)
        public (string Name, long Offset)? Find(long va)
        {
            int lo = 0, hi = map.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (map.Keys[mid] <= va) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (best < 0 || va - map.Keys[best] >= MaxFieldOffset) return null;
            return (map.Values[best], va - map.Keys[best]);
        }
    }

    // 그 게임 버전의 자료 (없거나 받지 못하면 null — 까닭은 why). 자료는 끝자리를 0000 으로 적는 일이 많아(패치 파일
    // 2025.06.28.0000.0001 -> data_2025.06.28.0000.0000) 같은 판이 없으면 끝자리만 0000 인 판도 본다.
    public static Symbols? Load(string version, out string why)
    {
        try
        {
            string? main = Http.GetStringOrNull($"{Raw}/data.yml");
            foreach (string v in Candidates(version))
            {
                string? text = main != null && VersionOf(main) == v ? main : Http.GetStringOrNull($"{Raw}/old/data_{v}.yml");
                if (text == null || VersionOf(text) != v) continue;
                why = "";
                return Parse(text);
            }
            why = $"{version} 판이 아직 없음";
            return null;
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            why = $"받지 못함 ({Errors.Describe(ex)})";
            return null;
        }
    }

    internal static IEnumerable<string> Candidates(string version)
    {
        yield return version;
        int dot = version.LastIndexOf('.');
        if (dot > 0 && version[(dot + 1)..] != "0000") yield return version[..dot] + ".0000";
    }

    internal static string? VersionOf(string text) =>
        Regex.Match(text, @"^version:\s*(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    // 줄 단위로 읽는다 (YAML 전체를 해석하지 않음): globals 의 "0x...: 이름", classes 의 "  <클래스>:" 아래 instances / vtbls 의 "- ea: 0x...".
    // '#' 뒤는 주석 ("#fail" 로 시작하는 줄은 찾지 못한 주소라 통째로 빠진다).
    internal static Symbols Parse(string text)
    {
        var map = new SortedList<long, string>();
        string? section = null, cls = null, sub = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split('#')[0].TrimEnd();
            if (line.Trim().Length == 0) continue;
            if (!line.StartsWith(' ')) { section = line.TrimEnd(':'); cls = sub = null; continue; }
            if (section == "globals")
            {
                if (Regex.Match(line, @"^\s+0x([0-9A-Fa-f]+)\s*:\s*(\S.*)$") is { Success: true } g && Hex(g.Groups[1].Value) is { } va)
                    map.TryAdd(va, g.Groups[2].Value.Trim());
            }
            else if (section == "classes")
            {
                if (Regex.Match(line, @"^  (\S.*):$") is { Success: true } c) { cls = c.Groups[1].Value; sub = null; }
                else if (Regex.Match(line, @"^    (\w+):") is { Success: true } s) sub = s.Groups[1].Value;
                else if (cls != null && sub is "instances" or "vtbls" && Regex.Match(line, @"-\s*ea:\s*0x([0-9A-Fa-f]+)") is { Success: true } e &&
                         Hex(e.Groups[1].Value) is { } va)
                    map.TryAdd(va, $"{cls} {(sub == "instances" ? "instance" : "vtbl")}");
            }
        }
        return new Symbols(map);
    }

    private static long? Hex(string s) =>
        long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out long v) ? v : null;

    // 두 판에 모두 있는 이름으로: 같은 이름의 같은 거리면 확인, 두 값이 모두 이름 붙은 주소 그 자체(거리 0)인데 이름이 다르면 불일치,
    // 그 밖(이름 없는 주소, 가까운 이름 + 거리가 다름, 같은 객체의 다른 자리)은 모름.
    public static (Verdict Verdict, string Detail) Compare(Symbols oldData, long oldVa, Symbols newData, long newVa)
    {
        var a = oldData.Common(newData).Find(oldVa);
        var b = newData.Common(oldData).Find(newVa);
        string pair = $"{Describe(a, oldVa)} -> {Describe(b, newVa)}";
        if (a is { } x && b is { } y)
        {
            if (x.Name == y.Name && x.Offset == y.Offset) return (Verdict.Confirmed, Describe(b, newVa));
            if (x.Name != y.Name && x.Offset == 0 && y.Offset == 0) return (Verdict.Mismatch, pair);
        }
        return (Verdict.Unknown, $"이름으로 가릴 수 없음 ({pair})");
    }

    private static string Describe((string Name, long Offset)? s, long va) =>
        s is not { } x ? $"0x{va:x}" : x.Offset == 0 ? x.Name : $"{x.Name}+0x{x.Offset:x}";
}
