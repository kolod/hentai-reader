namespace HentaiReader.Host.Tests;

public class SmokeTests
{
    [Fact]
    public void ContractVersion_IsSet() => Assert.Equal("1.1.1", HostInfo.ContractVersion);
}
