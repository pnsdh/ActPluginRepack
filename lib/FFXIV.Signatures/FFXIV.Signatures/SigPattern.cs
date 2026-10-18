using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FFXIV.Signatures;

// 패턴 표기
//   Pelite  deucalion 이 쓰는 pelite 문법의 부분집합 (아래). "?" 하나가 1바이트.
//   Hex     OverlayPlugin / cactbot 의 SigScan 표기: 공백 없이 두 글자가 1바이트, "??" 가 와일드카드 1바이트.
//           (공백은 무시한다. 대소문자 상관없음.)
// 주의: Hex 표기를 Parse(text) 에 그대로 넘기면 오류 없이 pelite 로 읽힌다 — "488B??05" 의 "??" 가 2바이트가 된다.
public enum SigSyntax { Pelite, Hex }

// pelite 패턴 문법의 부분집합을 .NET Regex 로 컴파일해 스캔한다.
// (문법: https://docs.rs/pelite/latest/pelite/pattern/fn.parse.html)
//
//   4C 8B      바이트 (공백은 의미 없음)
//   ?          임의의 1바이트 ("??" 는 2바이트)
//   **         임의의 1바이트 (플러그인 메모리 / Machina Oodle 시그니처 표기. pelite 의 '*' 는 미지원)
//   [n] [n-m]  n바이트 / n~m바이트 건너뜀 (non-greedy, n,m <= 16384)
//   ( a | b )  대안
//   '          북마크: 현재 위치 저장
//   $ { ' }    rel32 점프를 따라가 도착 주소 저장 (중괄호 안은 ' 만 지원)
//
// 바이트열은 Latin1 로 1:1 문자열 변환해 Regex 로 찾는다.
// null 인자는 ArgumentNullException, 그 밖의 잘못된 패턴은 SignatureException.
public sealed class SigPattern
{
    // Rva: 일치 시작, Length: 일치한 바이트 수, Saves: 북마크/점프 도착 주소 (RVA. 선택되지 않은 대안 쪽의 북마크는 0),
    // Section: 일치한 섹션 이름
    public sealed record Match(int Rva, IReadOnlyList<int> Saves, int Length = 0, string? Section = null)
    {
        public int End => Rva + Length;

        // 일치 구간 [Rva, End) 가 size 바이트 경계를 넘는지. 프로세스 메모리를 64KB(0x10000)씩 읽어 덩어리 안에서만
        // 찾는 스캐너는 경계에 걸친 일치를 놓친다 (모듈 기준 주소가 64KB 정렬이라 RVA 로 계산해도 같다).
        public bool CrossesBoundary(int size = 0x10000) =>
            size > 0 ? Length > 0 && Rva / size != (End - 1) / size : throw new ArgumentOutOfRangeException(nameof(size));

        // OverlayPlugin / cactbot 의 SigScan 이 이 일치를 놓치는지. 두 스캐너는 모듈을 64KB 덩어리로 (겹치지 않게) 읽고,
        // 덩어리마다 "일치 + max(0, offset) + 읽을 값(rip 4 / 아니면 8바이트)" 이 덩어리 안에 다 들어오는 일치만 본다:
        //   search_offset < n - L - max(0, offset) - (rip ? 4 : 8)
        // 그래서 경계 바로 앞에서 끝나는 일치도, 읽을 값이 경계를 넘으면 놓친다 (CrossesBoundary 보다 넓다).
        // (덩어리는 모두 꽉 찼다고 본다 — 모듈 끝의 덜 찬 마지막 덩어리는 따로 계산하지 않는다)
        public bool MissedByChunkedScan(int offset, bool rip, int chunk = 0x10000)
        {
            if (chunk <= 0) throw new ArgumentOutOfRangeException(nameof(chunk));
            long inChunk = Rva % chunk;
            return inChunk + Length + Math.Max(0, offset) + (rip ? 4 : 8) >= chunk;
        }
    }

    // 저장 슬롯 하나 = Regex 캡처 그룹 하나. Jump 면 4바이트 rel32, 아니면 폭 0.
    private sealed record Save(int Group, bool Jump);

    // pelite 표기의 패턴 (Hex 로 파싱했으면 바꾼 결과)
    public string Text { get; }
    private readonly Regex _regex;
    private readonly List<Save> _saves;

    private SigPattern(string text, Regex regex, List<Save> saves)
    {
        Text = text;
        _regex = regex;
        _saves = saves;
    }

    public static SigPattern Parse(string text, SigSyntax syntax) =>
        Parse(syntax == SigSyntax.Hex ? HexToPelite(text) : text);

    private const int MaxSkip = 16384;

    public static SigPattern Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokenize(text);
        void Fail(string why) => throw Error(text, why);

        var rx = new StringBuilder();
        var saves = new List<Save>();
        int group = 0, bytes = 0, fixedBytes = 0;
        bool skips = false;
        double width = 1; // 가변 건너뛰기 폭의 곱 — 일치 시작 하나에서 되짚기가 시도하는 길이 조합 수의 위 한계
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case TokenKind.Byte: rx.Append(@"\x").Append(t.Value.ToString("X2")); bytes++; fixedBytes++; break;
                case TokenKind.Wild: rx.Append('.'); bytes++; break;
                case TokenKind.Save: rx.Append("()"); saves.Add(new Save(++group, Jump: false)); break;
                case TokenKind.Open: rx.Append("(?:"); break;
                case TokenKind.Bar: rx.Append('|'); break;
                case TokenKind.Close: rx.Append(')'); break;
                case TokenKind.Skip:
                    rx.Append(t.Min == t.Max ? $".{{{t.Min}}}" : $".{{{t.Min},{t.Max}}}?");
                    bytes++; skips = true; width *= t.Max - t.Min + 1;
                    break;
                case TokenKind.Jump: rx.Append("(.{4})"); saves.Add(new Save(++group, Jump: true)); bytes += 4; break;
            }
        }
        if (bytes == 0) Fail("패턴이 비어 있음");
        // 와일드카드·건너뛰기만 있으면 모든 위치에 맞는다 (exe 하나에 수천만 개)
        if (fixedBytes == 0) Fail("고정 바이트가 하나도 없음 — 와일드카드만으로는 모든 곳에 맞음");
        // 가변 건너뛰기를 겹치면([0-16384] [0-16384]) 일치 시작마다 폭의 곱만큼 되짚는다 — 건너뛰기 하나의 최대 폭까지만 받는다
        if (width > MaxSkip + 1) Fail("건너뛰기 범위를 겹친 패턴 — 되짚기가 너무 많아 찾는 데 너무 오래 걸림");
        // 건너뛰기가 있으면 되짚기가 많아 컴파일한 쪽이 훨씬 빠르다 (결과는 같다)
        var options = RegexOptions.Singleline | RegexOptions.CultureInvariant | (skips ? RegexOptions.Compiled : 0);
        Regex regex;
        try { regex = new Regex(rx.ToString(), options, ScanTimeout); }
        catch (ArgumentException ex) { throw Error(text, ex.Message); }
        // [0], (4C|) 처럼 0바이트에도 맞는 패턴은 모든 위치에 맞는다 (exe 하나에 수천만 개)
        if (regex.IsMatch("")) Fail("0바이트에도 맞는 패턴 — 고정 바이트나 와일드카드가 하나는 있어야 함");
        // 고정 바이트가 대안의 한쪽에만 있어 다른 쪽으로 비켜 가는 패턴((48|?) ?, (4C|) ?) 도 모든 위치에 맞는다 — 고정 바이트를
        // 하나도 거치지 않는 길이 있는지를 구조로 가른다 (패턴 길이와 상관없이 정확하다)
        if (AvoidsFixedBytes(tokens)) Fail("모든 위치에 맞는 패턴 — 고정 바이트가 대안의 한쪽에만 있음");
        // 앞에 넓은 건너뛰기가 있는 패턴([0-16384] 48)은 일치 시작마다 되짚기가 많다 — 같은 바이트로 채운 데이터에서 짧은 시간
        // 안에 찾기를 끝내는지로 가른다 (서로 다른 바이트 넷에 모두 맞는 패턴도 여기서 거른다). 컴파일하지 않은 쪽으로 재
        // 기계 속도에 따라 결과가 흔들리지 않게 한다.
        bool universal;
        try
        {
            var probe = new Regex(rx.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, ProbeTimeout);
            universal = UniformProbes.All(p => probe.IsMatch(p));
        }
        catch (RegexMatchTimeoutException) { Fail("건너뛰기가 너무 넓은 패턴 — 찾는 데 너무 오래 걸림"); return null!; }
        if (universal) Fail("모든 위치에 맞는 패턴 — 고정 바이트가 대안의 한쪽에만 있음");
        return new SigPattern(text.Trim(), regex, saves);
    }

    private static SignatureException Error(string text, string why) => new($"시그니처 형식이 잘못되었습니다: '{text}' ({why})");

    // 고정 바이트를 하나도 거치지 않고 끝까지 가는 길이 있는지 (대안은 어느 한쪽이라도, 차례는 모두가)
    private static bool AvoidsFixedBytes(List<Token> tokens)
    {
        var stack = new Stack<(bool AnyAlt, bool Current)>();
        bool anyAlt = false, current = true;
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case TokenKind.Byte: current = false; break;
                case TokenKind.Open: stack.Push((anyAlt, current)); anyAlt = false; current = true; break;
                case TokenKind.Bar: anyAlt |= current; current = true; break;
                case TokenKind.Close:
                    bool alt = anyAlt || current;
                    (anyAlt, current) = stack.Pop();
                    current &= alt;
                    break;
            }
        }
        return current;
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly string[] UniformProbes =
        [.. new[] { '\x00', '\xFF', '\x5A', '\xA5' }.Select(c => new string(c, 20 * 1024))]; // [n] 건너뛰기 최대(16384)보다 길게

    public static bool TryParse(string text, out SigPattern? pattern)
    {
        if (text == null) { pattern = null; return false; }
        try { pattern = Parse(text); return true; }
        catch (SignatureException) { pattern = null; return false; }
    }

    // Hex 표기 -> pelite 표기 ("488B??05" -> "48 8B ? 05")
    public static string HexToPelite(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        string s = string.Concat(hex.Where(c => !char.IsWhiteSpace(c)));
        if (s.Length == 0) throw Error(hex, "패턴이 비어 있음");
        if (s.Length % 2 != 0) throw Error(hex, "글자 수가 홀수 — 두 글자가 1바이트");
        var tokens = new List<string>(s.Length / 2);
        for (int i = 0; i < s.Length; i += 2)
        {
            char a = s[i], b = s[i + 1];
            if (a == '?' && b == '?') tokens.Add("?");
            else if (Uri.IsHexDigit(a) && Uri.IsHexDigit(b)) tokens.Add(s.Substring(i, 2).ToUpperInvariant());
            else throw Error(hex, $"{i}번째 글자부터 '{a}{b}' 는 바이트도 \"??\" 도 아님");
        }
        return string.Join(" ", tokens);
    }

    // pelite 표기 -> Hex 표기. 바이트·와일드카드(?, **)는 그대로, "$ { ' }" 는 rel32 4바이트 와일드카드, 북마크(')는 뺀다.
    // 대안 ( | ) 과 건너뛰기 [n] 은 Hex 로 나타낼 수 없어 오류.
    public static string PeliteToHex(string pattern)
    {
        Parse(pattern); // 문법 확인 (null 이면 ArgumentNullException)
        var sb = new StringBuilder();
        foreach (var t in Tokenize(pattern))
        {
            switch (t.Kind)
            {
                case TokenKind.Byte: sb.Append(t.Value.ToString("X2")); break;
                case TokenKind.Wild: sb.Append("??"); break;
                case TokenKind.Save: break;
                case TokenKind.Jump: sb.Append("????????"); break;
                default:
                    throw new SignatureException($"'{pattern}' 은(는) Hex 표기로 바꿀 수 없습니다 ('{pattern[t.Start]}' — 대안/건너뛰기는 Hex 에 없음).");
            }
        }
        return sb.ToString();
    }

    // ---- 토큰 (Parse / PeliteToHex / SigRegen 이 함께 쓴다) ----

    internal enum TokenKind { Byte, Wild, Save, Open, Bar, Close, Skip, Jump }

    // Start/End: 원문에서의 자리 [Start, End). Byte 는 Value, Skip 은 Min/Max (고정 길이면 같음).
    internal readonly record struct Token(TokenKind Kind, int Start, int End, byte Value = 0, int Min = 0, int Max = 0);

    // pelite 표기를 토큰으로 나눈다. 괄호 짝까지 확인한다 (빈 패턴·고정 바이트 없음 같은 의미 검사는 Parse 가 한다).
    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int depth = 0, i = 0;
        void Fail(string why) => throw Error(text, why);

        while (i < text.Length)
        {
            char c = text[i];
            if (c == ' ') { i++; continue; }
            if (Uri.IsHexDigit(c))
            {
                if (i + 1 >= text.Length || !Uri.IsHexDigit(text[i + 1])) Fail($"{i}번째 글자의 16진수가 두 자리가 아님");
                tokens.Add(new Token(TokenKind.Byte, i, i + 2, Value: Convert.ToByte(text.Substring(i, 2), 16)));
                i += 2;
                continue;
            }
            switch (c)
            {
                case '?': tokens.Add(new Token(TokenKind.Wild, i, i + 1)); i++; break;
                case '*':
                    if (i + 1 >= text.Length || text[i + 1] != '*') Fail("* 는 \"**\" (와일드카드 1바이트)로만 쓸 수 있음");
                    tokens.Add(new Token(TokenKind.Wild, i, i + 2)); i += 2;
                    break;
                case '\'': tokens.Add(new Token(TokenKind.Save, i, i + 1)); i++; break;
                case '(': tokens.Add(new Token(TokenKind.Open, i, i + 1)); depth++; i++; break;
                case '|':
                    if (depth == 0) Fail("'|' 가 괄호 밖에 있음");
                    tokens.Add(new Token(TokenKind.Bar, i, i + 1)); i++;
                    break;
                case ')':
                    if (--depth < 0) Fail("')' 의 짝이 맞지 않음");
                    tokens.Add(new Token(TokenKind.Close, i, i + 1)); i++;
                    break;
                case '[':
                {
                    int end = text.IndexOf(']', i);
                    if (end < 0) Fail("'[' 가 닫히지 않음");
                    // 숫자만 (공백·부호는 받지 않는다 — 그대로 정규식에 넣으면 "{ 2 }" 처럼 글자로 읽혀 엉뚱하게 맞는다)
                    var range = text[(i + 1)..end].Split('-')
                        .Select(r => int.TryParse(r, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v)
                                     && v <= MaxSkip ? v : -1).ToArray();
                    if (range.Length is < 1 or > 2 || range.Any(v => v < 0)) Fail("[n-m] 건너뛰기 형식이 잘못됨");
                    if (range.Length == 2 && range[0] >= range[1]) Fail("[n-m] 은 n < m 이어야 함");
                    tokens.Add(new Token(TokenKind.Skip, i, end + 1, Min: range[0], Max: range[^1]));
                    i = end + 1;
                    break;
                }
                case '$':
                {
                    // "$ { ' }" 만 지원: 점프 도착지의 서브패턴은 북마크 하나뿐이어야 한다.
                    string rest = text[(i + 1)..].Replace(" ", "");
                    if (!rest.StartsWith("{'}", StringComparison.Ordinal)) Fail("'$' 뒤에는 \"$ { ' }\" 만 쓸 수 있음");
                    int end = text.IndexOf('}', i) + 1;
                    tokens.Add(new Token(TokenKind.Jump, i, end));
                    i = end;
                    break;
                }
                default:
                    Fail($"{i}번째 글자 '{c}' 는 쓸 수 없음");
                    break;
            }
        }
        if (depth != 0) Fail("'(' 의 짝이 맞지 않음");
        return tokens;
    }

    // 건너뛰기를 겹친 패턴은 되짚기가 많을 수 있어 한 번 찾기에 시간 제한을 두고, 스캔 전체에도 따로 제한을 둔다
    // (일치마다는 빨라도 일치가 수천만 개면 끝나지 않는다)
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TotalScanTimeout = TimeSpan.FromSeconds(120);

    public List<Match> Scan(ScanTarget target) => Scan(target, TotalScanTimeout);

    internal List<Match> Scan(ScanTarget target, TimeSpan total)
    {
        ArgumentNullException.ThrowIfNull(target);
        try { return ScanCore(target, total); }
        catch (RegexMatchTimeoutException)
        {
            throw new SignatureException($"시그니처 스캔이 {ScanTimeout.TotalSeconds:0}초 안에 끝나지 않아 멈췄습니다 (건너뛰기가 너무 넓은 패턴): '{Text}'");
        }
    }

    private List<Match> ScanCore(ScanTarget target, TimeSpan total)
    {
        var result = new List<Match>();
        var clock = Stopwatch.StartNew();
        foreach (var seg in target.Segments)
        {
            for (var m = _regex.Match(seg.Text); m.Success; m = _regex.Match(seg.Text, m.Index + 1))
            {
                if (clock.Elapsed > total)
                    throw new SignatureException($"시그니처 스캔이 {total.TotalSeconds:0.#}초 안에 끝나지 않아 멈췄습니다 ({result.Count}곳까지 찾음 — 너무 흔한 패턴): '{Text}'");
                var saves = new List<int>(_saves.Count);
                foreach (var s in _saves)
                {
                    if (!m.Groups[s.Group].Success) { saves.Add(0); continue; } // 선택되지 않은 (a|b) 쪽의 북마크
                    int pos = m.Groups[s.Group].Index;
                    int rva = seg.Section.VirtualAddress + pos;
                    saves.Add(s.Jump ? rva + 4 + BitConverter.ToInt32(target.Bytes, seg.Section.RawOffset + pos) : rva);
                }
                result.Add(new Match(seg.Section.VirtualAddress + m.Index, saves, m.Length, seg.Section.Name));
            }
        }
        return result;
    }
}

// 스캔 범위
//   Text         .text 섹션만 (코드 시그니처의 기본)
//   AllSections  파일에 데이터가 있는 모든 섹션 — 프로세스 메모리의 모듈 전체를 훑는 런타임 스캐너가 보게 될 개수를
//                재려고. 섹션마다 따로 훑으므로 섹션 경계에 걸친 일치, PE 헤더, 파일에 없는 가상 꼬리는 보지 않는다.
public enum ScanScope { Text, AllSections }

// 스캔 대상 = exe 의 섹션들. Latin1 문자열 변환은 대상마다 한 번만 한다. 만든 뒤에는 읽기만 하므로 여러 스레드에서 같이 써도 된다.
public sealed class ScanTarget
{
    internal sealed record Segment(PeImage.Section Section, string Text);

    public string Path { get; }
    public PeImage Image { get; }
    public ScanScope Scope { get; }
    public byte[] Bytes => Image.Bytes;
    internal IReadOnlyList<Segment> Segments { get; }

    public ScanTarget(string path, ScanScope scope = ScanScope.Text) : this(PeImage.Load(path), path, scope) { }

    public ScanTarget(PeImage image, string path, ScanScope scope = ScanScope.Text)
    {
        ArgumentNullException.ThrowIfNull(image);
        Path = path;
        Image = image;
        Scope = scope;
        IEnumerable<PeImage.Section> sections = scope == ScanScope.Text
            ? [image.Require(".text")]
            : image.Sections.Where(s => s.FileSize > 0).OrderBy(s => s.VirtualAddress);
        Segments = sections.Select(s => new Segment(s, Encoding.Latin1.GetString(image.FileSpan(s)))).ToList();
    }
}
