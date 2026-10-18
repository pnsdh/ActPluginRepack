using System.Text.Json;

// 함께 쓰는 JSON 설정 (기록 파일들이 같은 모양으로 쓰고 읽게)
internal static class JsonOptions
{
    // 사람이 열어 볼 수 있게 들여 쓴다 (설정·기록 파일)
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // 손으로 고친 파일용: 주석과 끝 쉼표를 허용 (config.json)
    public static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // 주석만 허용 (OverlayPlugin opcodes.jsonc 틀 — 끝 쉼표 등 형식이 달라졌으면 그대로 옮기지 않고 멈춘다)
    public static readonly JsonDocumentOptions Comments = new() { CommentHandling = JsonCommentHandling.Skip };
}
