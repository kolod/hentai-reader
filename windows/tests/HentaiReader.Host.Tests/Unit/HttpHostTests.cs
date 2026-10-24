using System.Text;
using System.Text.Json.Nodes;
using HentaiReader.Host.Http;

namespace HentaiReader.Host.Tests.Unit;

/// <summary>Port of tools/runner/test/http.test.ts for the Windows HttpHost.</summary>
public class HttpHostTests
{
    private sealed class FakeTransport(Func<RawRequest, Task<RawResponse>> handler) : ITransport
    {
        public List<RawRequest> Sent { get; } = new();

        public Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default)
        {
            lock (Sent) Sent.Add(request);
            return handler(request);
        }
    }

    private static RawResponse Text(int status, string body, params (string, string)[] headers) =>
        new(status, [new("content-type", "text/plain; charset=utf-8"), .. headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2))],
            Encoding.UTF8.GetBytes(body));

    private static HttpHost Host(ITransport transport, bool enforce = false, Politeness? politeness = null)
    {
        var host = new HttpHost(transport, enforce);
        host.SetManifest(new HttpManifest(["a.test", "*.b.test"], politeness ?? new Politeness(0, 4)));
        return host;
    }

    private static string Meta(string url, string method = "GET", object? headers = null) =>
        new JsonObject { ["url"] = url, ["method"] = method, ["headers"] = headers is null ? null : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(headers)) }.ToJsonString();

    private static JsonObject Env(string env) => JsonNode.Parse(env)!.AsObject();

    [Theory]
    [InlineData("https://a.test/x", true)]
    [InlineData("https://img.b.test/x", true)]
    [InlineData("https://b.test/x", false)]
    [InlineData("https://sub.a.test/x", false)]
    [InlineData("https://evil.test/x", false)]
    public async Task Domain_allow_list(string url, bool allowed)
    {
        var host = Host(new FakeTransport(_ => Task.FromResult(Text(200, "ok"))));
        var env = Env((await host.RequestAsync(Meta(url), null)).Env);
        if (allowed) Assert.Equal(200, env["value"]!["status"]!.GetValue<int>());
        else Assert.Equal("InvalidInput", env["error"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Politeness_gap_and_concurrency()
    {
        var inFlight = 0;
        var maxSeen = 0;
        var transport = new FakeTransport(async _ =>
        {
            var n = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxSeen, n);
            await Task.Delay(40);
            Interlocked.Decrement(ref inFlight);
            return Text(200, "ok");
        });
        var host = Host(transport, enforce: true, new Politeness(25, 2));
        await Task.WhenAll(Enumerable.Range(1, 5).Select(i => host.RequestAsync(Meta($"https://a.test/{i}"), null)));
        Assert.True(maxSeen <= 2, $"max in flight {maxSeen}");
        var starts = host.Log.Select(r => r.StartedAt).Order().ToList();
        for (var i = 1; i < starts.Count; i++) Assert.True(starts[i] - starts[i - 1] >= 23, $"gap {starts[i] - starts[i - 1]}");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [Fact]
    public async Task Cookies_are_scoped_merged_and_hidden()
    {
        var transport = new FakeTransport(r => Task.FromResult(r.Url.EndsWith("/login") ? Text(200, "ok", ("set-cookie", "sid=abc; Path=/")) : Text(200, "ok")));
        var host = Host(transport);
        host.Cookies.Seed("img.b.test", "seeded=1");
        var login = Env((await host.RequestAsync(Meta("https://a.test/login"), null)).Env);
        await host.RequestAsync(Meta("https://a.test/next", headers: new Dictionary<string, string> { ["Cookie"] = "nw=1; sid=override" }), null);
        await host.RequestAsync(Meta("https://img.b.test/x"), null);
        Assert.Null(login["value"]!["headers"]!["set-cookie"]);
        Assert.False(transport.Sent[0].Headers.ContainsKey("cookie"));
        Assert.Equal("sid=override; nw=1", transport.Sent[1].Headers["cookie"]);
        Assert.Equal("seeded=1", transport.Sent[2].Headers["cookie"]);
    }

    [Fact]
    public async Task Redirects_are_followed_limited_and_rechecked()
    {
        var transport = new FakeTransport(r =>
        {
            var n = int.Parse(new Uri(r.Url).AbsolutePath[1..]);
            if (n == 2) return Task.FromResult(Text(302, "", ("location", "https://evil.test/away")));
            return Task.FromResult(n < 9 ? Text(302, "", ("location", $"/{n + 1}")) : Text(200, "end"));
        });
        var host = Host(transport);
        Assert.Equal("https://a.test/9", Env((await host.RequestAsync(Meta("https://a.test/7"), null)).Env)["value"]!["url"]!.GetValue<string>());
        Assert.Equal("Network", Env((await host.RequestAsync(Meta("https://a.test/3"), null)).Env)["error"]!["kind"]!.GetValue<string>());
        Assert.Equal("InvalidInput", Env((await host.RequestAsync(Meta("https://a.test/1"), null)).Env)["error"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Statuses_are_returned_and_bodies_validated()
    {
        var host = Host(new FakeTransport(_ => Task.FromResult(Text(404, "missing"))));
        var env = Env((await host.RequestAsync(Meta("https://a.test/x"), null)).Env);
        Assert.Equal((404, "missing"), (env["value"]!["status"]!.GetValue<int>(), env["value"]!["text"]!.GetValue<string>()));
        var bad = new JsonObject { ["url"] = "https://a.test/x", ["body"] = "no" }.ToJsonString();
        Assert.Equal("InvalidInput", Env((await host.RequestAsync(bad, null)).Env)["error"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Images_use_the_same_domain_check()
    {
        var host = Host(new FakeTransport(_ => Task.FromResult(new RawResponse(200, [], [1, 2, 3]))));
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(new byte[] { 1, 2, 3 }, await host.FetchImageAsync("https://img.b.test/p.webp", null, token));
        await Assert.ThrowsAnyAsync<Exception>(() => host.FetchImageAsync("https://evil.test/p.webp", null, token));
    }
}
