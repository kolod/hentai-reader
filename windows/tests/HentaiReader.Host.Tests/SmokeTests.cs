namespace HentaiReader.Host.Tests;

public class SmokeTests
{
    [Fact]
    public void ContractVersion_IsSet() => Assert.Equal("1.2.0", HostInfo.ContractVersion);
}
