using System.IO.Compression;
using Mono.Cecil;

// Costura.Fody 이중 내장 리소스 관련 상수/헬퍼.
// 새 플러그인 버전에서 리소스/Costura 이름이 바뀌면 여기 상수만 맞추면 된다.
internal static class Costura
{
    public const string Machina = "costura.machina.ffxiv.dll.compressed";
    public const string Resource = "costura.ffxiv_act_plugin.resource.dll.compressed";
    public const string OpcodePrefix = "Machina.FFXIV.Headers.Opcodes.";
    public const string GeneratedPrefix = "FFXIV_ACT_Plugin.Resource.Generated.";
    public const string OverridePrefix = GeneratedPrefix + "Override.";

    public static EmbeddedResource? Find(ModuleDefinition module, string name) =>
        module.Resources.OfType<EmbeddedResource>().FirstOrDefault(r => r.Name == name);

    // 리소스를 같은 자리에서 바꾼다 (순서 유지 — 플러그인이 리소스 순서대로 읽는 것이 있다: override 파일은 나중 것이 이긴다)
    public static void ReplaceInPlace(ModuleDefinition module, Resource existing, Resource replacement)
    {
        int i = module.Resources.IndexOf(existing);
        if (i < 0) throw new InvalidOperationException($"리소스가 없습니다: {existing.Name}");
        module.Resources[i] = replacement;
    }

    // 바깥 플러그인에서 안쪽 어셈블리를 풀어 읽기 전용으로 연다.
    public static ModuleDefinition ReadInner(ModuleDefinition plugin, string name)
    {
        var res = Find(plugin, name) ?? throw new ToolException($"리소스가 없습니다: {name}");
        return ModuleDefinition.ReadModule(new MemoryStream(Inflate(res.GetResourceData()), writable: false));
    }

    public static byte[] Inflate(byte[] data)
    {
        using var inMs = new MemoryStream(data, writable: false);
        using var ds = new DeflateStream(inMs, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var outMs = new MemoryStream();
        using (var ds = new DeflateStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        return outMs.ToArray();
    }
}
