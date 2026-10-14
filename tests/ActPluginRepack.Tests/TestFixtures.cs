using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ActPluginRepack.Tests;

// 섹션 여러 개짜리 최소 PE (deucalion .rdata / .rustc 시험용). 섹션은 0x200 정렬로 차례로 놓는다.
internal static class FakePeSections
{
    public static byte[] Build(params (string Name, byte[] Data)[] sections)
    {
        const int pe = 0x40, align = 0x200;
        int raw = align;
        var offsets = new List<int>();
        foreach (var (_, data) in sections)
        {
            offsets.Add(raw);
            raw += (data.Length + align - 1) / align * align;
        }
        var b = new byte[raw];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(pe).CopyTo(b, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(b, pe);
        BitConverter.GetBytes((ushort)sections.Length).CopyTo(b, pe + 6);
        BitConverter.GetBytes((ushort)0).CopyTo(b, pe + 20);
        for (int i = 0; i < sections.Length; i++)
        {
            int s = pe + 24 + i * 40;
            var (name, data) = sections[i];
            Encoding.ASCII.GetBytes(name).CopyTo(b, s);
            BitConverter.GetBytes(data.Length).CopyTo(b, s + 8);
            BitConverter.GetBytes(0x1000 * (i + 1)).CopyTo(b, s + 12);
            BitConverter.GetBytes(data.Length).CopyTo(b, s + 16);
            BitConverter.GetBytes(offsets[i]).CopyTo(b, s + 20);
            data.CopyTo(b, offsets[i]);
        }
        return b;
    }

    // 섹션 하나의 원문 (Latin1)
    public static string Section(byte[] pe, string name)
    {
        var image = PeImage.Parse(pe, "test");
        return Encoding.Latin1.GetString(image.Span(image.Require(name)));
    }
}

// Mono.Cecil 로 만드는 작은 .NET 어셈블리 (버전 / Costura 내장 리소스 / IL 문자열 시험용)
internal static class FakeAssembly
{
    public static AssemblyDefinition Create(string name, Version version)
    {
        var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, version), name, ModuleKind.Dll);
        asm.MainModule.Mvid = new Guid(0x12345678, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); // 같은 입력이면 같은 바이트
        return asm;
    }

    public static byte[] Bytes(AssemblyDefinition asm)
    {
        using var ms = new MemoryStream();
        asm.Write(ms);
        return ms.ToArray();
    }

    // 버전만 정한 빈 어셈블리. marker 를 바꾸면 버전은 같고 내용만 다른 파일이 된다.
    public static byte[] WithVersion(string name, string version, string marker = "")
    {
        using var asm = Create(name, Version.Parse(version));
        asm.MainModule.Resources.Add(new EmbeddedResource("marker", ManifestResourceAttributes.Public, Encoding.UTF8.GetBytes(marker)));
        return Bytes(asm);
    }

    // FFXIV_ACT_Plugin 모양: Costura 로 압축해 넣은 Memory.dll(시그니처 txt 리소스)과 Machina(SigScan IL 문자열)
    public static ModuleDefinition Plugin(string memoryGlobal, IReadOnlyList<(string Name, string Pattern)> oodleGlobal,
                                          IReadOnlyList<(string Name, string Pattern)> oodleKorean)
    {
        using var memory = Create("FFXIV_ACT_Plugin.Memory", new Version(1, 0));
        memory.MainModule.Resources.Add(new EmbeddedResource(PluginSignatures.MemoryResource("Global"),
            ManifestResourceAttributes.Public, Encoding.UTF8.GetBytes(memoryGlobal)));

        using var machina = Create("Machina.FFXIV", new Version(1, 0));
        var m = machina.MainModule;
        var names = oodleGlobal.Concat(oodleKorean).Select(x => x.Name).Distinct().ToList();
        var enumType = new TypeDefinition("Machina.FFXIV.Memory", "SignatureType",
            TypeAttributes.Public | TypeAttributes.Sealed, m.TypeSystem.Object);
        for (int i = 0; i < names.Count; i++)
            enumType.Fields.Add(new FieldDefinition(names[i], FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal,
                m.TypeSystem.Int32) { Constant = i });
        m.Types.Add(enumType);
        m.Types.Add(SigScanType(m, "SigScan", oodleGlobal, names));
        m.Types.Add(SigScanType(m, "KoreanSigScan", oodleKorean, names));

        var plugin = ModuleDefinition.CreateModule("FFXIV_ACT_Plugin", ModuleKind.Dll);
        plugin.Resources.Add(new EmbeddedResource(PluginSignatures.MemoryCostura, ManifestResourceAttributes.Private,
            Costura.Deflate(Bytes(memory))));
        plugin.Resources.Add(new EmbeddedResource(Costura.Machina, ManifestResourceAttributes.Private,
            Costura.Deflate(Bytes(machina))));
        return plugin;
    }

    // Signatures getter: ldc.i4 <종류> ; ldstr "<패턴>" ; pop ; pop ... ; ldnull ; ret
    private static TypeDefinition SigScanType(ModuleDefinition m, string name, IReadOnlyList<(string Name, string Pattern)> sigs, List<string> names)
    {
        var type = new TypeDefinition("Machina.FFXIV.Memory", name, TypeAttributes.Public | TypeAttributes.Class, m.TypeSystem.Object);
        var getter = new MethodDefinition("get_Signatures",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, m.TypeSystem.Object);
        var il = getter.Body.GetILProcessor();
        foreach (var (sig, pattern) in sigs)
        {
            il.Emit(OpCodes.Ldc_I4, names.IndexOf(sig));
            il.Emit(OpCodes.Ldstr, pattern);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Pop);
        }
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(getter);
        var prop = new PropertyDefinition("Signatures", PropertyAttributes.None, m.TypeSystem.Object) { GetMethod = getter };
        type.Properties.Add(prop);
        return type;
    }
}
