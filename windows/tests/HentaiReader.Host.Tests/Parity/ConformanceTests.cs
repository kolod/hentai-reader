using HentaiReader.Host.Engine;
using HentaiReader.Host.Fixtures;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Tests.Parity;

/// <summary>Single-defect conformance samples behave on Windows as in the runner.</summary>
public class ConformanceTests
{
    private static Task<SourceSession> Open(string sample)
    {
        var dir = Path.Combine(RepoPaths.Conformance, sample);
        var options = new SessionOptions(new OfflineTransport(Path.Combine(dir, "fixtures")), false, new SourceStorage());
        return SourceSession.OpenAsync(File.ReadAllText(Path.Combine(dir, sample + ".js")), sample + ".js", options);
    }

    [Fact]
    public async Task Forbidden_capability_is_named()
    {
        using var session = await Open("broken-forbidden-capability");
        var res = await session.CallAsync("search", [System.Text.Json.Nodes.JsonNode.Parse("""{"text":"","filters":{}}"""), null],
            token: TestContext.Current.CancellationToken);
        Assert.False(res.Ok);
        Assert.Equal("forbidden capability: fetch", res.Error!.Message);
    }

    [Fact]
    public async Task Incompatible_contract_version_is_refused()
    {
        var e = await Assert.ThrowsAsync<LoadException>(() => Open("broken-contract-version"));
        Assert.Contains("requires contract 2.0", e.Message);
    }
}
