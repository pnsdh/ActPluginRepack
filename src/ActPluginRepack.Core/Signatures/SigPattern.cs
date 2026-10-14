using System.Text;
using System.Text.RegularExpressions;

// deucalion 이 쓰는 pelite 패턴 문법의 부분집합을 .NET Regex 로 컴파일해 스캔한다.
// (문법: https://docs.rs/pelite/latest/pelite/pattern/fn.parse.html)
//
//   4C 8B      바이트 (공백은 의미 없음)
//   ?          임의의 1바이트 ("??" 는 2바이트)
//   **         임의의 1바이트 (플러그인 메모리 / Machina Oodle 시그니처 표기. pelite 의 '*' 는 미지원)
//   [n] [n-m]  n바이트 / n~m바이트 건너뜀 (non-greedy)
//   ( a | b )  대안
//   '          북마크: 현재 위치 저장
//   $ { ' }    rel32 점프를 따라가 도착 주소 저장 (중괄호 안은 ' 만 지원)
//
// 바이트열은 Latin1 로 1:1 문자열 변환해 Regex 로 찾는다.
internal sealed class SigPattern
{
    public sealed record Match(int Rva, IReadOnlyList<int> Saves, int Length = 0)
    {
        public int End => Rva + Length;
    }

    // 저장 슬롯 하나 = Regex 캡처 그룹 하나. Jump 면 4바이트 rel32, 아니면 폭 0.
    private sealed record Save(int Group, bool Jump);

    public string Text { get; }
    private readonly Regex _regex;
    private readonly List<Save> _saves;

    private SigPattern(string text, Regex regex, List<Save> saves)
    {
        Text = text;
        _regex = regex;
        _saves = saves;
    }

    public static SigPattern Parse(string text)
    {
        var rx = new StringBuilder();
        var saves = new List<Save>();
        int group = 0, depth = 0, bytes = 0;
        int i = 0;

        void Fail(string why) => throw new ToolException($"시그니처 형식이 잘못되었습니다: '{text}' ({why})");

        while (i < text.Length)
        {
            char c = text[i];
            if (c == ' ') { i++; continue; }
            if (Uri.IsHexDigit(c))
            {
                if (i + 1 >= text.Length || !Uri.IsHexDigit(text[i + 1])) Fail($"{i}번째 글자의 16진수가 두 자리가 아님");
                rx.Append(@"\x").Append(text, i, 2);
                i += 2; bytes++;
                continue;
            }
            switch (c)
            {
                case '?': rx.Append('.'); bytes++; i++; break;
                case '*':
                    if (i + 1 >= text.Length || text[i + 1] != '*') Fail("* 는 \"**\" (와일드카드 1바이트)로만 쓸 수 있음");
                    rx.Append('.'); bytes++; i += 2;
                    break;
                case '\'': rx.Append("()"); saves.Add(new Save(++group, Jump: false)); i++; break;
                case '(': rx.Append("(?:"); depth++; i++; break;
                case '|': if (depth == 0) Fail("'|' 가 괄호 밖에 있음"); rx.Append('|'); i++; break;
                case ')': if (--depth < 0) Fail("')' 의 짝이 맞지 않음"); rx.Append(')'); i++; break;
                case '[':
                {
                    int end = text.IndexOf(']', i);
                    if (end < 0) Fail("'[' 가 닫히지 않음");
                    string[] range = text[(i + 1)..end].Split('-');
                    if (range.Length is < 1 or > 2 || !range.All(r => int.TryParse(r, out int v) && v is >= 0 and <= 16384))
                        Fail("[n-m] 건너뛰기 형식이 잘못됨");
                    if (range.Length == 2 && int.Parse(range[0]) >= int.Parse(range[1])) Fail("[n-m] 은 n < m 이어야 함");
                    rx.Append(range.Length == 1 ? $".{{{range[0]}}}" : $".{{{range[0]},{range[1]}}}?");
                    bytes++; i = end + 1;
                    break;
                }
                case '$':
                {
                    // "$ { ' }" 만 지원: 점프 도착지의 서브패턴은 북마크 하나뿐이어야 한다.
                    string rest = text[(i + 1)..].Replace(" ", "");
                    if (!rest.StartsWith("{'}", StringComparison.Ordinal)) Fail("'$' 뒤에는 \"$ { ' }\" 만 쓸 수 있음");
                    rx.Append("(.{4})");
                    saves.Add(new Save(++group, Jump: true));
                    bytes += 4;
                    i = text.IndexOf('}', i) + 1;
                    break;
                }
                default:
                    Fail($"{i}번째 글자 '{c}' 는 쓸 수 없음");
                    break;
            }
        }
        if (depth != 0) Fail("'(' 의 짝이 맞지 않음");
        if (bytes == 0) Fail("패턴이 비어 있음");
        Regex regex;
        try { regex = new Regex(rx.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant); }
        catch (ArgumentException ex) { throw new ToolException($"시그니처 형식이 잘못되었습니다: '{text}' ({ex.Message})"); }
        return new SigPattern(text.Trim(), regex, saves);
    }

    public static bool TryParse(string text, out SigPattern? pattern)
    {
        try { pattern = Parse(text); return true; }
        catch (ToolException) { pattern = null; return false; }
    }

    public List<Match> Scan(ScanTarget target)
    {
        var result = new List<Match>();
        for (var m = _regex.Match(target.Text); m.Success; m = _regex.Match(target.Text, m.Index + 1))
        {
            var saves = new List<int>(_saves.Count);
            foreach (var s in _saves)
            {
                if (!m.Groups[s.Group].Success) { saves.Add(0); continue; } // 선택되지 않은 (a|b) 쪽의 북마크
                int pos = m.Groups[s.Group].Index;
                int rva = target.VirtualAddress + pos;
                saves.Add(s.Jump ? rva + 4 + BitConverter.ToInt32(target.Bytes, target.RawOffset + pos) : rva);
            }
            result.Add(new Match(target.VirtualAddress + m.Index, saves, m.Length));
        }
        return result;
    }
}

// 스캔 대상 = 게임 exe 의 .text 섹션. Latin1 문자열 변환은 exe 당 한 번만 한다.
internal sealed class ScanTarget
{
    public string Path { get; }
    public string Text { get; }
    public byte[] Bytes { get; }
    public int VirtualAddress { get; }
    public int RawOffset { get; }

    public ScanTarget(string path)
    {
        Path = path;
        var pe = PeImage.Load(path);
        var text = pe.Require(".text");
        Bytes = pe.Bytes;
        VirtualAddress = text.VirtualAddress;
        RawOffset = text.RawOffset;
        Text = Encoding.Latin1.GetString(pe.Span(text));
    }
}
