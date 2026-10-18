namespace ActPluginRepack.Tests;

// FFXIVClientStructs ida/data.yml 의 데이터 기호 읽기와 대조 규칙
public class ClientStructsDataTests
{
    private const string Yml = """
        version: 2026.10.06.0000.0000

        globals:
          0x142000000: g_First
        #fail   0x142000100: g_Missing
          0x142000200: g_Second # 주석
        functions:
          0x140001000: SomeFunction
        classes:
          Client::Game::Group::GroupManager:
            instances:
              - ea: 0x142A00000
                pointer: false
            funcs:
              0x140002000: ctor
          Client::UI::Misc::RaptureLogModule:
            vtbls:
              - ea: 0x142100000
        """;

    private static ClientStructsData.Symbols Data => ClientStructsData.Parse(Yml.Replace("\r\n", "\n"));

    [Fact]
    public void ReadsDataSymbolsOnly()
    {
        Assert.Equal("2026.10.06.0000.0000", ClientStructsData.VersionOf(Yml));
        var d = Data;
        Assert.Equal(4, d.Count); // g_First, g_Second, GroupManager instance, RaptureLogModule vtbl (함수·#fail 은 빠짐)
        Assert.Equal(("g_Second", 0L), d.Find(0x142000200));
        Assert.Equal(("Client::Game::Group::GroupManager instance", 0x14L), d.Find(0x142A00014));
        Assert.Equal(("Client::UI::Misc::RaptureLogModule vtbl", 0L), d.Find(0x142100000));
        Assert.Null(d.Find(0x140001000));   // 함수 주소 — 앞에 데이터 기호 없음
        Assert.Null(d.Find(0x142A02000));   // 기호에서 너무 멂
    }

    [Fact]
    public void SameSymbolAndOffsetIsConfirmed() =>
        Assert.Equal(ClientStructsData.Verdict.Confirmed, ClientStructsData.Compare(Data, 0x142A00014, Data, 0x142A00014).Verdict);

    // 버리는 것은 두 값이 모두 이름 붙은 주소 그 자체인데 이름이 다를 때뿐
    [Fact]
    public void ExactOtherSymbolIsMismatch() =>
        Assert.Equal(ClientStructsData.Verdict.Mismatch, ClientStructsData.Compare(Data, 0x142A00000, Data, 0x142000200).Verdict);

    // 가까운 이름 + 거리로는 버리지 않는다 (이름 없는 객체가 옆 이름에 붙은 것일 수 있다), 같은 객체의 다른 자리·이름 없는 주소도 모름
    [Theory]
    [InlineData(0x142A00014, 0x142000204)]
    [InlineData(0x142A00014, 0x142A00018)]
    [InlineData(0x142A00014, 0x142A05000)]
    public void OtherwiseUnknown(long oldVa, long newVa) =>
        Assert.Equal(ClientStructsData.Verdict.Unknown, ClientStructsData.Compare(Data, oldVa, Data, newVa).Verdict);

    // 한쪽 판에만 있는 이름은 쓰지 않는다: 옛 판에는 같은 주소에 global 이 있고 다음 판에서 빠지면(#fail) 같은 자리가 다른 이름으로
    // 잡혀 옳은 후보를 버리게 된다 (2025.06.28 -> 07.30 의 GameObjectManager_ObjectList, MobArray)
    [Fact]
    public void NameInOnlyOneVersionIsIgnored()
    {
        var old = ClientStructsData.Parse(Yml.Replace("\r\n", "\n").Replace("globals:\n", "globals:\n  0x142A00020: g_ObjectList\n"));
        Assert.Equal(ClientStructsData.Verdict.Confirmed, ClientStructsData.Compare(old, 0x142A00020, Data, 0x142A00020).Verdict);
    }
}
