// 클라이언트 지역. 이름은 SharpVision 의 추출 폴더명(output/extract/<name>)과 같다.
//   Label             화면에 보이는 한국어 이름
//   SharpVisionLangs  SharpVision --lang 코드
//   CsvLeafs          input\csv\<leaf> 폴더명 (SharpVisionLangs 와 같은 순서)
//   Opcodes           input\opcodes\<Opcodes>.txt / Machina 의 opcode 리소스 / OverlayPlugin opcodes.jsonc 지역명
//   CnOpcodes         CN 클라이언트와 opcode 가 같음 (7.5 통합 클라이언트) -> Yarukon CN 결과를 쓸 수 있다
//   Karashiiro        karashiiro/FFXIVOpcodes 의 region
//   Lemegeton         Lemegeton blueprint 의 Region Name (없으면 출력 안 함)
internal sealed record Region(string Name, string Label, string[] SharpVisionLangs, string[] CsvLeafs, string Opcodes, bool CnOpcodes,
                              string Karashiiro, string? Lemegeton)
{
    // All 보다 먼저 선언해야 한다 (정적 필드는 적힌 순서대로 초기화된다)
    public static readonly Region Global =
        new("global", "글로벌", ["en", "ja", "de", "fr"], ["en", "ja", "de", "fr"], "Global", CnOpcodes: true, "Global", "EN/DE/FR/JP");
    public static readonly Region SimplifiedChinese = new("simplified-chinese", "중국", ["chs"], ["cn"], "Chinese", CnOpcodes: true, "CN", "CN");
    public static readonly Region Korean = new("korean", "한국", ["ko"], ["ko"], "Korean", CnOpcodes: true, "KR", "KR");
    public static readonly Region TraditionalChinese =
        new("traditional-chinese", "대만", ["cht"], ["tc"], "TraditionalChinese", CnOpcodes: false, "TW", null);

    public static readonly Region[] All = [Global, SimplifiedChinese, Korean, TraditionalChinese];

    // 공개 추출본(xiv-data-oxidizer 출력, fae-industries/xiv-data)의 언어 폴더 이름 — input\csv\<leaf> 와 cn 만 다르다
    public static string DataLang(string leaf) => leaf == "cn" ? "chs" : leaf;

    public static Region? Find(string name) =>
        All.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // 지역을 모르는 클라이언트(직접 고른 폴더 등)도 화면에 보일 수 있게
    public static string LabelOf(Region? region) => region?.Label ?? "지역 모름";
}
