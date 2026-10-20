// prepare 의 CSV 단계: 클라이언트마다 필요한 시트만 input\csv\<lang> 로.
// 설치된 클라이언트에서 직접 추출(항상 정확)이 우선: xiv-data-oxidizer(설정 시) > SharpVision(설정 경로 > GitHub
// 최신 릴리스). 둘 다 안 되면 공개 데이터마이닝 저장소. 앞의 것이 실패하면 다음 것으로 넘어간다.
// 클라이언트가 설정에 없는 cn/tc 도 Action/Status 는 공개 저장소에서 받는다 (플러그인의 cn/tc 리소스가 그 둘뿐).
// 이번 버전 것을 구하지 못한 언어가 있으면 끝에 ToolException — prepare 가 그 단계를 경고로 보여 준다
// (빌드는 되지만 지난 패치의 이름이 들어가므로 조용히 넘기지 않는다).
internal sealed class CsvCollector(Workspace ws, AppConfig config, IReadOnlySet<string> sources, bool overwrite)
{
    // --sources 이름
    public static readonly string[] SourceNames = ["oxidizer", "sharpvision", "datamining", "xivdata"];

    private bool _oxidizerTried;
    private Oxidizer.Tool? _oxidizer;
    private bool _sharpVisionTried;
    private string? _sharpVision;

    // 언어 폴더(input\csv\<leaf>) -> 이번에 받은 출처 (출처 보고용). 못 구한 언어는 그렇다고
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Sources => _sources;

    public void Collect(GameClient client)
    {
        var region = client.Region!;
        if (OxidizerTool() is { } oxidizer)
        {
            try
            {
                string cache = Oxidizer.ExtractCsv(ws, oxidizer, client, overwrite);
                CopySheets(region, leaf => Path.Combine(cache, leaf), "xiv-data-oxidizer (설치된 게임에서 추출)");
                return;
            }
            catch (Exception ex) when (Errors.Recoverable(ex)) { Log.Warn($"[csv] xiv-data-oxidizer 실패 - {Errors.Describe(ex)}"); }
        }
        if (SharpVisionExe() is { } sharpVision)
        {
            try
            {
                CollectWithSharpVision(sharpVision, client);
                return;
            }
            catch (Exception ex) when (Errors.Recoverable(ex)) { Log.Warn($"[csv] SharpVision 실패 - {Errors.Describe(ex)}"); }
        }
        ThrowIfStale(region.CsvLeafs.Where(leaf => !FromDatamining(leaf, client.Version, LeafSheets(leaf))).ToList());
    }

    // 클라이언트가 설정에 없는 cn/tc: cn 은 지금 게임 버전(versionOf)으로 맞추고, tc 는 최신본
    public void CollectChinese(Func<Region, string?> versionOf)
    {
        var stale = new List<string>();
        foreach (var region in Region.All.Where(r => config.Clients.All(c => c.Region != r)))
        foreach (string leaf in region.CsvLeafs.Where(CsvConverter.IsChineseLeaf))
        {
            Log.Section($"[{region.Name}] (클라이언트 설정 없음)");
            if (!FromDatamining(leaf, region.CnOpcodes ? versionOf(region) : null, CsvConverter.ChineseSheets)) stale.Add(leaf);
        }
        ThrowIfStale(stale);
    }

    private void ThrowIfStale(List<string> leafs)
    {
        foreach (string leaf in leafs) _sources[leaf] = "이번 버전 것을 구하지 못해 전에 받은 것을 그대로 씁니다.";
        if (leafs.Count > 0)
            throw new ToolException($@"{string.Join(", ", leafs.Select(l => $@"input\csv\{l}"))} 는 이번 게임 버전의 데이터를 구하지 못해 전에 받은 것을 그대로 둡니다. " +
                                    "빌드는 되지만 스킬·상태 이름이 지난 패치 것으로 들어갈 수 있으니 나중에 다시 실행하세요.");
    }

    // 필요할 때 한 번만 준비한다 (oxidizer 는 받기+빌드, SharpVision 은 릴리스 받기)
    private Oxidizer.Tool? OxidizerTool()
    {
        if (_oxidizerTried) return _oxidizer;
        _oxidizerTried = true;
        if (config.Oxidizer == null || !sources.Contains("oxidizer")) return null;
        try { _oxidizer = Oxidizer.Prepare(ws, config.Oxidizer); }
        catch (Exception ex) when (Errors.Recoverable(ex)) { Log.Warn($"[csv] xiv-data-oxidizer 를 쓸 수 없음 - {Errors.Describe(ex)}"); }
        return _oxidizer;
    }

    private string? SharpVisionExe()
    {
        if (_sharpVisionTried) return _sharpVision;
        _sharpVisionTried = true;
        if (!sources.Contains("sharpvision")) return null;
        try { _sharpVision = config.SharpVision ?? SharpVisionRelease.Resolve(ws); }
        catch (Exception ex) when (Errors.Recoverable(ex)) { Log.Warn($"[csv] SharpVision 을 쓸 수 없음 - {Errors.Describe(ex)}"); }
        return _sharpVision;
    }

    private void CollectWithSharpVision(string exe, GameClient client)
    {
        var region = client.Region!;
        if (!File.Exists(exe)) throw new ToolException($"SharpVision 이 없습니다: {exe}");

        string src = SharpVision.CsvRoot(exe, client);
        if (overwrite || SharpVision.MissingSheets(src, region).Count > 0)
        {
            Log.Note($"[csv] SharpVision 으로 추출 ({string.Join(",", region.SharpVisionLangs)})");
            SharpVision.ExtractCsv(exe, client, overwrite);
        }
        else Log.Note($"[csv] 기존 추출본 사용: {src}");

        var missing = SharpVision.MissingSheets(src, region);
        if (missing.Count > 0)
            throw new ToolException($"SharpVision 으로 추출한 {src} 에 빠진 시트가 있습니다: {string.Join(", ", missing)}");

        CopySheets(region, leaf => Path.Combine(src, region.SharpVisionLangs[Array.IndexOf(region.CsvLeafs, leaf)]), "SharpVision (설치된 게임에서 추출)");
    }

    // 추출본의 필요한 시트만 input\csv\<leaf> 로
    private void CopySheets(Region region, Func<string, string> sourceDir, string source)
    {
        foreach (string leaf in region.CsvLeafs)
        {
            string dst = ws.CsvLeafDir(leaf);
            Directory.CreateDirectory(dst);
            foreach (string sheet in CsvConverter.RequiredSheets)
            {
                string to = Path.Combine(dst, sheet + ".csv");
                File.Copy(Path.Combine(sourceDir(leaf), sheet + ".csv"), to + ".tmp", overwrite: true);
                File.Move(to + ".tmp", to, overwrite: true);
            }
            Log.Info($@"[csv] input\csv\{leaf} <- {CsvConverter.RequiredSheets.Length}개 시트");
            _sources[leaf] = source;
        }
    }

    private static string[] LeafSheets(string leaf) =>
        CsvConverter.IsChineseLeaf(leaf) ? CsvConverter.ChineseSheets : CsvConverter.RequiredSheets;

    // 공개 저장소를 순서대로 보고 버전이 맞는 첫 것에서 받는다. 받았으면 true.
    private bool FromDatamining(string leaf, string? gameVersion, string[] sheets)
    {
        var candidates = Datamining.For(leaf).Where(s => sources.Contains(s.Name)).ToList();
        if (candidates.Count == 0)
        {
            Log.Warn($@"[csv] {leaf}: 쓸 수 있는 출처 없음 - input\csv\{leaf} 는 그대로");
            return false;
        }
        foreach (var src in candidates)
        {
            Datamining.Status status;
            try { status = Datamining.Check(src, gameVersion); }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                status = new Datamining.Status($"{src.Repo} 확인 실패 ({Errors.Describe(ex)})", false);
            }
            if (!status.Usable)
            {
                Log.Warn($"[csv] {leaf}: {status.Description}");
                continue;
            }
            Log.Note($"[csv] {leaf}: {status.Description}");
            // 받다가 실패하면(막힘, 끊김) 다음 저장소로 — Download 는 전부 받은 뒤에만 바꾸므로 반쪽 폴더는 없다
            try { Datamining.Download(src, sheets, ws.CsvLeafDir(leaf), status.Sha); } // 버전을 확인한 그 커밋에서
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                Log.Warn($"[csv] {leaf}: {src.Repo} 에서 받지 못함 ({Errors.Describe(ex)}) - 다음 저장소");
                continue;
            }
            Log.Info($@"[csv] input\csv\{leaf} <- {sheets.Length}개 시트 ({src.Name})");
            _sources[leaf] = status.Description;
            return true;
        }
        Log.Warn($@"[csv] {leaf}: 쓸 수 있는 공개 저장소 없음 (버전 불일치 또는 받기 실패) - input\csv\{leaf} 는 그대로 (나중에 다시 실행)");
        return false;
    }
}
