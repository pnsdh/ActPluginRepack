using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// deucalion(패킷 후킹 dll, Rust) 의 시그니처를 읽고 고친다.
//
// 시그니처는 Rust 상수 문자열이라 dll 안에 두 벌 있다:
//   .rdata  실제로 쓰이는 사본. 문자열끼리 구분자 없이 이어 붙어 있다.
//   .rustc  메타데이터 사본. "..." 로 감싸져 있어 경계가 분명하다 -> 목록은 여기서 뽑는다.
// 패치는 두 사본 모두 같은 길이로 덮어쓴다(길이가 바뀌면 이어 붙은 옆 문자열이 깨짐).
// pelite 패턴은 공백을 무시하므로 짧으면 공백으로 채우고, 길면 공백을 빼서 맞춘다.
internal static class Deucalion
{
    public sealed record Sig(string Name, string Pattern);

    // deucalion 의 HookType 순서. .rdata 에 이 순서로 이어 붙어 있다.
    private static readonly string[] HookNames = ["Recv", "Send", "SendLobby"];

    // deucalion 이 후크를 걸 때 요구하는 정확한 일치 수 (hook/recv.rs·send.rs·send_lobby.rs 의 setup — 다르면 SignatureMatchFailed 로
    // 후크를 걸지 않는다. Recv 는 일치 순서대로 Chat / Lobby / Zone). 일치마다 하나로 센다 (procloader.rs find_pattern_matches —
    // call 대상이 같아도 일치 수다). CreateTarget 은 패턴 말고도 확인하는 것이 있어 여기 없다 (1곳 이상이면 씀).
    // 이름은 대소문자를 가리지 않는다 (input 의 이름처럼)
    public static int? RequiredMatches(string hookName) => Required.TryGetValue(hookName, out int n) ? n : null;

    private static readonly Dictionary<string, int> Required = new(StringComparer.OrdinalIgnoreCase) { ["Recv"] = 3, ["Send"] = 2, ["SendLobby"] = 1 };

    // 이 일치 수로 deucalion 이 그 후크를 걸 수 있는지 (요구 수가 없는 것은 1곳 이상)
    public static bool Usable(string hookName, int matches) => RequiredMatches(hookName) is { } n ? matches == n : matches > 0;

    private static readonly Regex QuotedPattern = new("\"([0-9A-Fa-f?$'{}()|\\[\\]\\- ]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex HexByte = new("[0-9A-Fa-f]{2}", RegexOptions.CultureInvariant);
    private static readonly Regex CreateTargetLabel = new(@"^ *Internal (\w+) sig error", RegexOptions.CultureInvariant);

    // input\plugin 의 deucalion 배포본. 플러그인이 시작할 때 *.distrib.dll -> *.dll 로 바꿔 쓴다.
    // 여러 개면 같은 폴더 플러그인의 Machina _checksum 과 짝인 것 — 공식 3.0.2.3 zip 에는 대만판용 옛 1.2.1 과 다른 서버용 1.5.0 이
    // 같이 들어 있었다 (사용자). 이 도구가 다루는 한국·글로벌은 _checksum 과 짝인 쪽을 쓴다.
    public static string? FindDistrib(string pluginDir)
    {
        if (!Directory.Exists(pluginDir)) return null;
        var found = Directory.GetFiles(pluginDir, "deucalion*.distrib.dll");
        return found.Length switch
        {
            0 => null,
            1 => found[0],
            _ => Paired(pluginDir, found) ?? throw new ToolException(
                $"{pluginDir} 에 deucalion distrib dll 이 여러 개 있고, 플러그인과 짝인 것을 가리지 못했습니다. 하나만 두세요: " +
                string.Join(", ", found.Select(Path.GetFileName))),
        };
    }

    // 플러그인(같은 폴더의 FFXIV_ACT_Plugin.dll)의 Machina _checksum 과 맞는 하나 (없거나 둘 이상이면 null)
    private static string? Paired(string pluginDir, string[] found)
    {
        string plugin = Path.Combine(pluginDir, PluginRelease.PluginFile);
        if (!File.Exists(plugin)) return null;
        try
        {
            using var module = Repacker.ReadPlugin(plugin);
            using var machina = Costura.ReadInner(module, Costura.Machina);
            if (DeucalionMachina.ReadChecksum(machina) is not { } expected) return null;
            var hits = found.Where(f => DeucalionMachina.Matches(expected, File.ReadAllBytes(f))).ToList();
            if (hits.Count != 1) return null;
            Log.Note($"[deucalion] distrib 이 여러 개 - 플러그인과 짝인 {Path.GetFileName(hits[0])} 을(를) 씀");
            return hits[0];
        }
        catch (Exception ex) when (ex is ToolException || Errors.IsFileError(ex)) { return null; }
    }

    public static List<Sig> ReadSignatures(byte[] dll)
    {
        var pe = SigLib.Pe(dll, "deucalion dll");
        var rustc = pe.Find(".rustc")
                    ?? throw new ToolException("deucalion dll 에 .rustc 섹션이 없어 시그니처 위치를 찾을 수 없습니다.");
        string meta = Encoding.Latin1.GetString(pe.Span(rustc));
        string rdata = Encoding.Latin1.GetString(pe.Span(pe.Require(".rdata")));

        var patterns = QuotedPattern.Matches(meta)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(p => HexByte.Matches(p).Count >= 4 && SigPattern.TryParse(p, out _))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // 공식 릴리스 빌드는 .rustc 메타데이터가 압축돼 있다 -> .rdata 의 이어 붙은 문자열을 잘라서 쓴다
        if (patterns.Count == 0) patterns = SplitRdata(rdata);

        // .rdata 의 실제 사본 위치로 이름을 붙인다:
        //   뒤에 "Internal <X> sig error" 가 오면 CreateTarget.<X> (normal/alt/fallback 후보)
        //   나머지는 이어 붙은 순서대로 Recv / Send / SendLobby
        var result = new List<Sig>();
        var hooks = new List<(int Offset, string Pattern)>();
        foreach (string p in patterns)
        {
            int at = rdata.IndexOf(p, StringComparison.Ordinal);
            if (at < 0) continue; // 런타임에 안 쓰이는 메타데이터 전용 문자열
            var label = CreateTargetLabel.Match(rdata, at + p.Length, Math.Min(64, rdata.Length - at - p.Length));
            if (label.Success) result.Add(new Sig($"CreateTarget.{label.Groups[1].Value}", p));
            else hooks.Add((at, p));
        }
        hooks.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        // 순서는 링커 배치일 뿐이라 내용으로도 확인한다: Recv 만 call 을 따라가는 형태, Send/SendLobby 는 함수 시작
        bool known = hooks.Count == HookNames.Length && hooks[0].Pattern.Contains('$') &&
                     hooks.Skip(1).All(h => !h.Pattern.Contains('$'));
        // 이름을 가리지 못하면 Hook1.. 로 — 그러면 후크별 일치 개수 규칙(RequiredMatches)을 쓸 수 없으므로 알린다 (deucalion 의 시그니처
        // 구성이 바뀐 것: 옛 1.4.x 는 4개였다)
        if (!known && hooks.Count > 0)
            Log.Warn($"[deucalion] 후크 시그니처 {hooks.Count}개의 이름(Recv/Send/SendLobby)을 가리지 못해 Hook1~{hooks.Count} 로 부름 - 일치 개수 규칙 없이 1곳 이상이면 정상으로 봄");
        result.InsertRange(0, hooks.Select((h, i) => new Sig(known ? HookNames[i] : $"Hook{i + 1}", h.Pattern)));
        if (result.Count == 0) throw new ToolException("deucalion dll 에서 시그니처를 찾지 못했습니다.");
        return result;
    }

    // .rdata 에서 시그니처처럼 생긴 문자열 구간을 찾아, 구분자 없이 이어 붙은 곳을 자른다.
    // 시그니처 토큰은 공백으로 나뉘므로, 한 덩어리 안에서 "1C40"(16진수 두 쌍)이나 "}40"/")40"/"?40"(닫는 기호나
    // 와일드카드 뒤 16진수)처럼 붙은 곳이 다음 문자열의 시작이다.
    private static readonly Regex PatternRun = new(@"[0-9A-F?$'{}()| ]{12,}", RegexOptions.CultureInvariant);

    private static List<string> SplitRdata(string rdata)
    {
        var result = new List<string>();
        foreach (Match run in PatternRun.Matches(rdata))
        {
            var current = new List<string>();
            foreach (string token in run.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string rest = token;
                var cut = Regex.Match(rest, @"^(.*?(?:[0-9A-F]{2}|[})?]))([0-9A-F]{2}.*)$");
                while (cut.Success && !Regex.IsMatch(rest, @"^[0-9A-F]{2}$"))
                {
                    current.Add(cut.Groups[1].Value);
                    Flush();
                    rest = cut.Groups[2].Value;
                    cut = Regex.Match(rest, @"^(.*?(?:[0-9A-F]{2}|[})?]))([0-9A-F]{2}.*)$");
                }
                current.Add(rest);
            }
            Flush();

            void Flush()
            {
                string p = string.Join(" ", current).Trim();
                current.Clear();
                if (HexByte.Matches(p).Count >= 4 && SigPattern.TryParse(p, out _)) result.Add(p);
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    // 여러 시그니처를 한 번에 바꾼다. 위치는 원본에서 한 번만 읽는다 — 패치한 dll 을 다시 읽으면(특히 .rustc 가
    // 압축돼 .rdata 를 잘라 읽는 릴리스 빌드에서) 공백을 줄이거나 채운 문자열의 경계가 달라질 수 있다.
    // 바뀐 게 없으면 Dll 은 null. Changed 는 실제로 바뀐 이름들.
    public static (byte[]? Dll, List<string> Changed) PatchAll(byte[] dll, IEnumerable<Sig> replacements)
    {
        var sigs = ReadSignatures(dll);
        byte[] patched = (byte[])dll.Clone();
        var pe = SigLib.Pe(patched, "deucalion dll");
        var changed = new List<string>();
        foreach (var r in replacements)
        {
            var sig = sigs.FirstOrDefault(s => s.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ToolException($"deucalion 에 '{r.Name}' 시그니처가 없습니다 " +
                                                 $"(쓸 수 있는 것: {string.Join(", ", sigs.Select(s => s.Name))}).");
            string old = sig.Pattern;
            // 띄어쓰기만 다른 건 같은 시그니처 (체크섬을 괜히 바꾸지 않게)
            if (r.Pattern.Replace(" ", "") == old.Replace(" ", "")) continue;
            string replacement = FitLength(r.Pattern.Trim(), old.Length, r.Name);
            if (ReplaceAll(patched, pe.Require(".rdata"), old, replacement) == 0)
                throw new ToolException($".rdata 에서 '{r.Name}' 시그니처를 찾지 못했습니다.");
            ReplaceAll(patched, pe.Require(".rustc"), $"\"{old}\"", $"\"{replacement}\"");
            changed.Add(sig.Name);
        }
        return (changed.Count > 0 ? patched : null, changed);
    }

    private static string FitLength(string pattern, int length, string name)
    {
        SigLib.Parse(pattern); // 문법 오류는 여기서
        if (pattern.Length > length) pattern = pattern.Replace(" ", "");
        if (pattern.Length > length)
            throw new ToolException($"'{name}' 시그니처는 공백을 빼도 너무 깁니다 " +
                                    $"({pattern.Length} > {length}자, dll 안의 문자열은 늘릴 수 없음).");
        return pattern.PadRight(length);
    }

    private static int ReplaceAll(byte[] bytes, PeImage.Section section, string from, string to)
    {
        byte[] f = Encoding.Latin1.GetBytes(from), t = Encoding.Latin1.GetBytes(to);
        var span = bytes.AsSpan(section.RawOffset, Math.Min(section.RawSize, bytes.Length - section.RawOffset));
        int count = 0;
        for (int i = span.IndexOf(f); i >= 0; )
        {
            t.CopyTo(span[i..]);
            count++;
            int next = span[(i + f.Length)..].IndexOf(f);
            i = next < 0 ? -1 : i + f.Length + next;
        }
        return count;
    }
}

// Machina.FFXIV.dll 안의 deucalion 관련 상수 (IL 의 ldstr).
//   DeucalionInjector._checksum      deucalion dll 의 SHA256 ("32-6b-..." 형식). 다르면 주입 거부.
//   DeucalionClient 의 시그니처 문자열  런타임 RECV 시그니처(플러그인 3.0.3.3+). deucalion 이
//                                    hello 에 "RECV OFF" 를 보내면 이 문자열로 다시 후킹시킨다.
internal static class DeucalionMachina
{
    private const string InjectorType = "Machina.FFXIV.Deucalion.DeucalionInjector";
    private const string ClientType = "Machina.FFXIV.Deucalion.DeucalionClient";

    // deucalion dll 내용으로 만든 _checksum 값 (SHA256 을 "32-6b-..." 형식으로 — 16진수 해시와 형식이 다르다)
    public static string ChecksumOf(byte[] dll) => BitConverter.ToString(SHA256.HashData(dll)).ToLowerInvariant();

    public static string? ReadChecksum(ModuleDefinition machina) => ChecksumLoad(machina)?.Operand as string;

    // deucalion dll 이 Machina 의 _checksum(expected — ReadChecksum 값)과 짝인지
    public static bool Matches(string? expected, byte[] dll) => expected == ChecksumOf(dll);

    // 짝이 아니면 ToolException — 짝이 안 맞으면 ACT 에서 패킷을 못 받는다. _checksum 을 못 찾았으면(null) 확인하지 않는다.
    // distrib: 안내에 쓸 deucalion 파일 경로
    public static void CheckPair(string? expected, byte[] dll, string distrib)
    {
        if (expected != null && !Matches(expected, dll))
            throw new ToolException($"{Path.GetFileName(distrib)} 이(가) 이 플러그인의 deucalion 이 아닙니다 (Machina _checksum 불일치). " +
                                    $"{Path.GetDirectoryName(distrib)} 에는 플러그인과 같은 릴리스의 deucalion 만 두세요.");
    }

    public static void SetChecksum(ModuleDefinition machina, string checksum)
    {
        var ins = ChecksumLoad(machina)
                  ?? throw new ToolException($"Machina 에 {InjectorType}._checksum 이 없습니다.");
        ins.Operand = checksum;
    }

    private static Instruction? ChecksumLoad(ModuleDefinition machina)
    {
        var getter = machina.GetType(InjectorType)?.Properties.FirstOrDefault(p => p.Name == "_checksum")?.GetMethod;
        return getter?.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr);
    }

    // DeucalionClient 안에서 시그니처처럼 생긴 ldstr 들. 없으면 런타임 교체 기능이 없는 버전.
    public static List<Instruction> RuntimeSignatures(ModuleDefinition machina) =>
        machina.GetType(ClientType)?.Methods
            .Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && LooksLikeSignature(s))
            .ToList() ?? [];

    private static bool LooksLikeSignature(string s) =>
        Regex.Matches(s, "[0-9A-Fa-f]{2}").Count >= 4 && SigPattern.TryParse(s, out _);
}
