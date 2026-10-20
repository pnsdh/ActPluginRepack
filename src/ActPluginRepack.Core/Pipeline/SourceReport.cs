using System.Text;
using System.Text.Json;

// 출처 보고: 마지막 prepare 가 무엇을 어디서 가져왔는지 — 게임 exe, 베이스 플러그인, 지역별 opcode 와 다른 출처와의 대조,
// 교체 시그니처, 게임 데이터(CSV), 만든 extras. 화면의 [출처], CLI sources, extras\sources.txt 가 같은 내용을 보여 준다.
// input\.sources.json 에 마지막 prepare 것만 남긴다. 문장은 만들 때 정해 둔다 (보여 주는 쪽은 그대로 늘어놓기만).
// Opcodes / Csv 가 null 이면 그 단계를 이번 prepare 에서 하지 않은 것 (Signatures 는 늘 있다 — 점검하지 않았으면 그렇다고 적는다).
internal sealed record SourceReport(
    DateTime PreparedAt,
    string Verdict,
    List<SourceReport.Item> Games,
    string? PluginVersion,
    List<SourceReport.Item>? Opcodes,
    List<SourceReport.Item>? Signatures,
    List<SourceReport.Item>? Csv,
    List<string> Extras)
{
    // Name: 지역·항목 이름, Text: 한 줄 설명, Details: 그 아래 들여 쓸 줄 (opcode 대조 결과 등)
    // Warning: 확인할 것 (교차 확인에서 출처·수단끼리 다름 등) — 화면이 눈에 띄게, hotfix 가 경고로
    // Unchecked: 교차 확인에서 대조할 짝이 없었던 것 — hotfix 가 "모두 대조함"으로 적지 않게
    public sealed record Item(string Name, string Text, List<string>? Details = null, bool Warning = false, bool Unchecked = false);

    // 교차 확인 (prepare 의 교차 확인 모드에서만, 아니면 null): 지역별 opcode 출처끼리, 시그니처마다 다른 수단과의 대조
    public List<Item>? CrossCheck { get; init; }

    public const string FileName = ".sources.json", TextFile = "sources.txt";

    public static string PathOf(Workspace ws) => Path.Combine(ws.Input, FileName);
    public static string TextPathOf(Workspace ws) => Path.Combine(ws.ExtrasDir, TextFile);

    // 없거나 읽지 못하면 null
    public static SourceReport? Load(Workspace ws)
    {
        string path = PathOf(ws);
        if (!File.Exists(path)) return null;
        // (손으로 고친 파일의 null 목록은 빈 것으로 — 화면이 멈추지 않게)
        try
        {
            return JsonSerializer.Deserialize<SourceReport>(File.ReadAllText(path), JsonOptions.Indented) is { } r
                ? r with { Games = r.Games ?? [], Extras = r.Extras ?? [] } : null;
        }
        catch (Exception ex) when (ex is JsonException || Errors.IsFileError(ex)) { return null; }
    }

    public void Save(Workspace ws)
    {
        Files.WriteAtomic(PathOf(ws), JsonSerializer.Serialize(this, JsonOptions.Indented));
        Directory.CreateDirectory(ws.ExtrasDir);
        Files.WriteAtomic(TextPathOf(ws), ToText());
    }

    // (제목, 줄들) — 화면은 칸마다, 텍스트는 제목 아래 줄로
    public List<(string Title, List<Item> Items)> Sections()
    {
        static List<Item> NotRun() => [new("", "이번 확인에서 하지 않았습니다 (빌드는 input 에 남은 이전 파일을 씁니다).")];
        var sections = new List<(string, List<Item>)>
        {
            ("게임", Games),
            ("공식 플러그인", [new("", PluginVersion is { } v ? $"FFXIV_ACT_Plugin {v}" : "없음")]),
            ("opcode", Opcodes ?? NotRun()),
            ("시그니처", Signatures ?? NotRun()),
            ("게임 데이터 (CSV)", Csv ?? NotRun()),
        };
        if (CrossCheck != null) sections.Add(("교차 확인", CrossCheck.Count > 0 ? CrossCheck : [new("", "대조할 것이 없었습니다.")]));
        if (Extras.Count > 0) sections.Add(("다른 도구용 파일 (extras)", Extras.Select(e => new Item("", e)).ToList()));
        return sections;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"출처 보고 - {PreparedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine(Verdict);
        foreach (var (title, items) in Sections())
        {
            sb.AppendLine().AppendLine($"[{title}]");
            foreach (var item in items)
            {
                sb.AppendLine(item.Name.Length > 0 ? $"  {StatusLine.Format(item.Name, item.Text, 12)}" : $"  {item.Text}");
                foreach (string d in item.Details ?? []) sb.AppendLine($"      {d}");
            }
        }
        return sb.ToString();
    }

    // ---- prepare 가 만드는 줄 ----

    // 보여 줄 지역 순서 (이 도구의 주 사용자 쪽 먼저)
    private static readonly Region[] ReportOrder = [Region.Global, Region.Korean, Region.SimplifiedChinese, Region.TraditionalChinese];

    // 지역별 opcode: 고른 출처와 바뀐 수, 다른 출처와 대조한 결과. 이번 버전 출처가 없는 지역은 공식 플러그인 값 그대로
    // configured: 이번에 쓴 클라이언트의 지역 — 그 지역을 못 구했으면 input\opcodes 의 이전 파일이 남아 빌드에 들어갈 수 있다
    public static List<Item> OpcodeItems(IReadOnlyDictionary<Region, OpcodeCollector.Outcome> outcomes,
                                         IReadOnlyDictionary<Region, List<OpcodeCollector.Comparison>> comparisons,
                                         IReadOnlyCollection<Region> unconfirmed, IReadOnlySet<Region> configured)
    {
        var items = new List<Item>();
        foreach (var region in ReportOrder)
        {
            if (!outcomes.TryGetValue(region, out var o))
            {
                items.Add(configured.Contains(region)
                    ? new(region.Label, @"구하지 못했습니다. input\opcodes 에 이전 파일이 남아 있으면 빌드가 그것을 씁니다.", Warning: true)
                    : new(region.Label, "이번 버전 것으로 확인된 출처가 없어 공식 플러그인 값을 그대로 씁니다."));
                continue;
            }
            var (source, filled) = OpcodeSet.SplitFilled(o.Source);
            string text = $"{source}: {o.Total}개 중 {o.Changed}개가 바뀌었습니다." +
                          (filled ? " 빠진 항목은 다른 출처로 채웠습니다." : "") +
                          (o.Missing > 0 ? $" {o.Missing}개는 출처에 없어 공식 값을 그대로 씁니다." : "") +
                          (unconfirmed.Contains(region) ? " 공개 자료 없이 정한 값이라 공개되면 다시 확인하세요." : "");
            var details = comparisons.GetValueOrDefault(region)?.Select(c => c.Differences.Count == 0
                ? $"= {c.Other}: {c.Compared}개 대조, 모두 같음"
                : $"≠ {c.Other}: {c.Compared}개 대조, {c.Differences.Count}개 다름 ({string.Join(", ", c.Differences)})").ToList();
            items.Add(new(region.Label, text, details is { Count: > 0 } ? details : ["다른 출처 없음 (대조하지 못함)"]));
        }
        return items;
    }

    // 교차 확인한 opcode: 지역마다 견준 출처(이번 버전 것으로 확인된 것)와 출처끼리 다른 항목, 참고(확인 안 된 출처).
    // hasIda: IDA 를 쓸 수 있었는지 — 쓸 수 있었는데 그 지역에 IDA 결과가 없으면(실패) 대조가 빠진 것으로
    public static List<Item> CrossCheckOpcodeItems(IEnumerable<OpcodeCollector.CrossResult> results, bool hasIda)
    {
        var items = new List<Item>();
        foreach (var r in results)
        {
            var refs = r.Reference.Select(x => $"참고 {x}").ToList();
            if (r.Sources.Count == 0)
            {
                // 이번 버전 출처가 없으면 그 지역은 공식 값 그대로라 고친 것이 없다 — 대조가 빠진 것으로 보지 않는다.
                // 그런데도 고쳐 썼으면 이번 버전인지 확인되지 않은 출처(karashiiro)로 정한 것 — 대조가 빠진 것
                items.Add(r.Used
                    ? new(r.Region.Label, "opcode: 이번 버전으로 확인된 출처 없이 정해 대조하지 못했습니다.", refs, Unchecked: true)
                    : new(r.Region.Label, "opcode: 이번 버전 출처가 없어 대조하지 못했습니다. 공식 플러그인 값을 그대로 씁니다.", refs));
                continue;
            }
            bool idaMissing = hasIda && r.IdaPossible && !r.HasIda;
            // 클라이언트가 없고 같은 버전을 직접 추출한 것도 없는 지역은 공개 자료끼리만 — 대조가 빠진 것으로 보지 않고 적어만 둔다
            if (!r.IdaPossible) refs.Add("참고 클라이언트가 없어 직접 추출(IDA·로컬 opcodediff)과는 대조하지 못함");
            string text = r.Sources.Count < 2 ? "opcode: 출처가 하나뿐이라 대조하지 못했습니다."
                : r.Differences.Count == 0 ? $"opcode: 출처 {r.Sources.Count}개의 {r.Compared}개 항목이 모두 같습니다."
                : $"opcode: 출처 {r.Sources.Count}개의 {r.Compared}개 항목 중 {r.Differences.Count}개가 다릅니다.";
            if (idaMissing) text += " IDA 결과는 없습니다.";
            items.Add(new(r.Region.Label, text, [$"출처: {string.Join(", ", r.Sources)}", .. r.Differences.Select(d => $"≠ {d}"), .. refs],
                          Warning: r.Differences.Count > 0, Unchecked: r.Sources.Count < 2 || idaMissing));
        }
        if (!hasIda) items.Add(new("", "IDA 를 쓸 수 없어(없거나 꺼 둠) IDA 직접 추출은 대조에 넣지 못했습니다.", Unchecked: true));
        return items;
    }

    // 교차 확인한 게임 데이터: 언어마다 견준 출처와 다른 이름 목록, 쓰지 못한 출처
    public static List<Item> CrossCheckCsvItems(IEnumerable<CsvCrossCheck.Result> results)
    {
        var items = new List<Item>();
        foreach (var r in results)
        {
            // (고른 것 — input\csv — 은 새로 구한 추출본을 복사한 것일 수 있어 독립된 출처로 세지 않는다)
            int independent = r.Sources.Count(s => s != CsvCrossCheck.ChosenLabel);
            string text = independent < 2 ? $"게임 데이터: 독립된 출처가 {(independent == 0 ? "없어" : "하나뿐이라")} 대조하지 못했습니다."
                : r.Differences.Count == 0 ? $"게임 데이터: 출처 {r.Sources.Count}곳의 이름 목록 {r.Files}개가 모두 같습니다."
                : $"게임 데이터: 출처 {r.Sources.Count}곳의 이름 목록 {r.Files}개 중 {r.Differences.Count}건이 다릅니다.";
            items.Add(new($"CSV {r.Leaf}", text,
                [$"출처: {string.Join(", ", r.Sources)}", .. r.Differences.Select(d => $"≠ {d}"), .. r.Notes.Select(n => $"- {n}")],
                Warning: r.Differences.Count > 0, Unchecked: independent < 2));
        }
        return items;
    }

    // 교체 시그니처 (빌드가 실제로 넣는 input\signatures)와 그 출처. 하나도 없으면 공식 그대로
    public static List<Item> SignatureItems(Workspace ws, bool? hooksOfficial)
    {
        var items = new List<Item>();
        foreach (var (kind, file) in new[] { ("패킷 후크", SignatureInput.FileName), ("메모리", SignatureInput.MemoryFile), ("Oodle", SignatureInput.OodleFile) })
            foreach (var (name, comment) in SignatureInput.LoadWithComments(ws.SignaturesDir, file))
                items.Add(new($"{kind} {name}", SourceText(comment)));
        if (items.Count == 0) items.Add(new("", "모두 공식 플러그인의 시그니처를 그대로 씁니다."));
        else if (hooksOfficial == true && items.Any(i => i.Name.StartsWith("패킷 후크", StringComparison.Ordinal)))
            items.Insert(0, new("", "공식 시그니처도 맞지만 넣어 둔 교체 시그니처를 씁니다."));
        return items;
    }

    // 시그니처 줄 위의 "# auto: ..." (SignatureRepair 가 남긴 출처) -> 읽을 문장
    internal static string SourceText(string? comment)
    {
        if (comment == null) return "직접 넣은 것";
        var m = System.Text.RegularExpressions.Regex.Match(comment, @"^deucalion main ([0-9a-f]{7,40})");
        if (m.Success) return $"upstream deucalion 커밋 {m.Groups[1].Value} ({comment})";
        m = System.Text.RegularExpressions.Regex.Match(comment, @"^deucalion release (\S+)");
        if (m.Success) return $"upstream deucalion 릴리스 {m.Groups[1].Value} ({comment})";
        if (comment.StartsWith("sigregen", StringComparison.Ordinal)) return $"직전 버전 exe 와 비교해 다시 만듦 ({comment})";
        return comment;
    }
}
