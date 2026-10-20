namespace ActPluginRepack.Gui.ViewModels;

// 고급 모드에서 고른 작업과 옵션 — gui.json 에 저장해 다음 실행에도 그대로 (AdvancedViewModel 이 바뀔 때마다 저장).
// 속성의 초기값이 곧 기본값이다 ([선택 초기화]도 new AdvancedOptions()). "캐시 무시하고 다시 추출"은 한 번만 쓰는 것이라 넣지 않는다.
// 배포 묶음의 패치 번호도 그때그때 다르므로 넣지 않는다.
internal sealed class AdvancedOptions
{
    // 입력 데이터 준비
    public bool UpdatePlugin { get; set; } = true;
    public bool Sigcheck { get; set; } = true;
    public bool Repair { get; set; } = true;
    public bool Opcodes { get; set; } = true;
    public bool Csv { get; set; } = true;
    public bool ForceIda { get; set; }
    public string? IdaTarget { get; set; }      // null: 설정(config.json)의 기본값을 따른다
    public bool ServerExe { get; set; }
    public bool ServerAllRegions { get; set; }
    public bool CrossCheck { get; set; }
    // 빌드 / 설치
    public bool DoRsv { get; set; }
    public bool DoBuild { get; set; } = true;
    public bool BuildCsv { get; set; } = true;
    public bool DoHotfix { get; set; }
    public bool DoInstall { get; set; }
    public bool InstallOfficial { get; set; }
    public bool InstallOverlay { get; set; } = true;
    // 끈 데이터 소스 (출처 이름 — prepare --sources 의 이름)
    public List<string> DisabledSources { get; set; } = [];

    // 기본값과 다른 선택을 사람이 읽을 말로 (화면의 "기본과 다른 선택" 줄 — 저장된 선택이 다음 패치 날까지 남아 결과를 조용히
    // 바꾸지 않게 보이도록). sourceLabel: 출처 이름 -> 화면 이름, idaLabel: IDA 분석 대상 지역 이름 -> 화면 이름
    // hasIda: IDA 를 쓸 수 있는지 (없으면 꺼진 칸이라 IDA 분석 대상은 적지 않는다)
    public List<string> Differences(Func<string, string> sourceLabel, Func<string, string> idaLabel, bool hasIda = true)
    {
        var d = new AdvancedOptions();
        var list = new List<string>();
        void Off(bool now, bool def, string name) { if (now != def) list.Add(now ? name : $"{name} 끔"); }
        Off(UpdatePlugin, d.UpdatePlugin, "공식 플러그인 받기");
        Off(Sigcheck, d.Sigcheck, "시그니처 점검");
        if (Sigcheck) Off(Repair, d.Repair, "자동 복구");
        Off(Opcodes, d.Opcodes, "opcode 수집");
        if (!ServerExe) Off(Csv, d.Csv, "CSV 수집");
        if (ForceIda) list.Add("IDA 결과 우선");
        if (IdaTarget != null && hasIda) list.Add($"IDA 분석 대상 {idaLabel(IdaTarget)}");
        if (ServerExe) list.Add(ServerAllRegions ? "공식 패치 서버 exe (설치하지 않은 지역까지)" : "공식 패치 서버 exe");
        if (CrossCheck) list.Add("교차 확인");
        Off(DoRsv, d.DoRsv, "rsv");
        Off(DoBuild, d.DoBuild, "build");
        // (공식 패치 서버 exe 로 준비하면 빌드가 이름 데이터를 넣지 않아 꺼진 칸이다)
        if (DoBuild && !(ServerExe && (UpdatePlugin || Sigcheck || Opcodes))) Off(BuildCsv, d.BuildCsv, "CSV 다시 변환");
        Off(DoHotfix, d.DoHotfix, "hotfix");
        Off(DoInstall, d.DoInstall, "설치");
        if (DoInstall && InstallOfficial) list.Add("공식 최신본 설치");
        if (DoInstall && !InstallOfficial) Off(InstallOverlay, d.InstallOverlay, "OverlayPlugin opcode 넣기");
        if (DisabledSources.Count > 0) list.Add($"데이터 소스 {DisabledSources.Count}개 끔 ({string.Join(", ", DisabledSources.Select(sourceLabel))})");
        return list;
    }
}
