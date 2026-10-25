using System.Text.Json.Nodes;
using HentaiReader.Host.Http;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;
using HentaiReader.Host.Tests.Parity;

namespace HentaiReader.Host.Tests.Http;

/// <summary>HTTP 429/509 on an image stops the source until the user retries (FR-021, Constitution VI).</summary>
public class ImageLimitTests
{
    private sealed class Transport : ITransport
    {
        public int Status { get; set; } = 200;
        public List<(string, string)> Headers { get; } = new();
        public int Requests { get; private set; }

        public Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default)
        {
            Requests++;
            return Task.FromResult(new RawResponse(Status, Headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)).ToList(), [1, 2, 3]));
        }
    }

    private static async Task<SourceSession> Open(Transport transport)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Conformance, "minimal", "minimal.js"), TestContext.Current.CancellationToken);
        return await SourceSession.OpenAsync(script, "minimal.js", new SessionOptions(transport, EnforcePoliteness: false, new SourceStorage()));
    }

    private static JsonObject Image(string url) => new() { ["url"] = url, ["headers"] = new JsonObject { ["referer"] = "https://example.test/" } };

    [Theory]
    [InlineData(429)]
    [InlineData(509)]
    public async Task Limit_status_puts_the_session_into_Limited(int status)
    {
        var transport = new Transport { Status = status };
        transport.Headers.Add(("retry-after", "120"));
        using var session = await Open(transport);

        var e = await Assert.ThrowsAsync<SourceLimitedException>(() => session.FetchImageAsync(Image("https://example.test/p/1.webp"), TestContext.Current.CancellationToken));
        Assert.Equal(120, e.RetryAfterSeconds);
        Assert.Equal(SessionState.Limited, session.State);
    }

    [Fact]
    public async Task Nothing_is_requested_while_limited_until_ClearLimit()
    {
        var transport = new Transport { Status = 509 };
        using var session = await Open(transport);
        await Assert.ThrowsAsync<SourceLimitedException>(() => session.FetchImageAsync(Image("https://example.test/p/1.webp"), TestContext.Current.CancellationToken));
        var sent = transport.Requests;

        transport.Status = 200;
        await Assert.ThrowsAsync<SourceLimitedException>(() => session.FetchImageAsync(Image("https://example.test/p/2.webp"), TestContext.Current.CancellationToken));
        var call = await session.CallAsync("search", [new JsonObject { ["text"] = "", ["filters"] = new JsonObject() }, null], token: TestContext.Current.CancellationToken);
        Assert.False(call.Ok);
        Assert.Equal(sent, transport.Requests);

        session.ClearLimit();
        Assert.Equal([1, 2, 3], await session.FetchImageAsync(Image("https://example.test/p/2.webp"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Other_errors_do_not_limit_the_source()
    {
        var transport = new Transport { Status = 404 };
        using var session = await Open(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => session.FetchImageAsync(Image("https://example.test/p/1.webp"), TestContext.Current.CancellationToken));
        Assert.NotEqual(SessionState.Limited, session.State);
    }

    [Fact]
    public async Task Retry_after_as_a_date_is_ignored()
    {
        var transport = new Transport { Status = 429 };
        transport.Headers.Add(("retry-after", "Wed, 21 Oct 2026 07:28:00 GMT"));
        using var session = await Open(transport);
        var e = await Assert.ThrowsAsync<SourceLimitedException>(() => session.FetchImageAsync(Image("https://example.test/p/1.webp"), TestContext.Current.CancellationToken));
        Assert.Null(e.RetryAfterSeconds);
    }
}
