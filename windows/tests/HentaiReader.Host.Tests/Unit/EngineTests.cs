using System.Text.Json;
using System.Text.Json.Nodes;
using HentaiReader.Host.Engine;

namespace HentaiReader.Host.Tests.Unit;

public class EngineTests
{
    private static Task<SourceEngine> Create(string code, FakeBridge? bridge = null, long? frozenNow = null, EngineLimits? limits = null) =>
        SourceEngine.CreateAsync($"{FakeBridge.Manifest()}\n{code}", "inline.js", bridge ?? new FakeBridge(), frozenNow, limits);

    private static async Task<JsonNode?> Value(SourceEngine engine, string fn, string args = "[]")
    {
        var res = await engine.CallAsync(fn, args, CancellationToken.None);
        Assert.True(res.Ok, res.Error?.Message);
        return res.Value;
    }

    [Fact]
    public async Task Reads_module_exports()
    {
        using var engine = await Create("export function a() {} export const b = 1;");
        var d = await engine.DescribeAsync();
        Assert.Equal(["a"], d.Functions);
        Assert.Equal("t", d.Manifest["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Concurrent_host_promises_resolve_in_any_order()
    {
        var delays = new Dictionary<string, int> { ["1"] = 60, ["2"] = 5, ["3"] = 30 };
        var bridge = new FakeBridge
        {
            Http = async (meta, _) =>
            {
                var url = JsonNode.Parse(meta)!["url"]!.GetValue<string>();
                var key = url[^1..];
                await Task.Delay(delays[key]);
                return FakeBridge.Text(url, key);
            },
        };
        using var engine = await Create("""
            export async function f() {
              const r = await Promise.all([1, 2, 3].map((i) => http.request({ url: "https://a.test/" + i })));
              return r.map((x) => x.body).join("");
            }
            """, bridge);
        Assert.Equal("123", (await Value(engine, "f"))!.GetValue<string>());
    }

    [Fact]
    public async Task Bytes_cross_the_bridge_both_ways()
    {
        byte[]? sent = null;
        var bridge = new FakeBridge
        {
            Http = (meta, body) =>
            {
                sent = body;
                return Task.FromResult(FakeBridge.Bytes("https://a.test/b", [0, 1, 254, 255]));
            },
        };
        using var engine = await Create("""
            export async function f() {
              const r = await http.request({ method: "POST", url: "https://a.test/b", body: new Uint8Array([7, 8, 9]), responseType: "bytes" });
              return [r.body instanceof Uint8Array, Array.from(r.body)];
            }
            """, bridge);
        var v = (await Value(engine, "f"))!.AsArray();
        Assert.True(v[0]!.GetValue<bool>());
        Assert.Equal("[0,1,254,255]", v[1]!.ToJsonString());
        Assert.Equal(new byte[] { 7, 8, 9 }, sent);
    }

    [Fact]
    public async Task Frozen_clock_is_visible()
    {
        var t = DateTimeOffset.Parse("2026-01-02T03:04:05Z").ToUnixTimeMilliseconds();
        using var engine = await Create("export function f() { return [Date.now(), new Date().toISOString(), new Date(0).getTime()]; }", frozenNow: t);
        Assert.Equal($"[{t},\"2026-01-02T03:04:05.000Z\",0]", (await Value(engine, "f"))!.ToJsonString());
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("setTimeout")]
    [InlineData("URL")]
    [InlineData("TextEncoder")]
    [InlineData("require")]
    [InlineData("process")]
    [InlineData("System")]
    [InlineData("importNamespace")]
    public async Task Capabilities_outside_the_contract_are_named(string name)
    {
        using var engine = await Create($"export function f() {{ return {name}; }}");
        var res = await engine.CallAsync("f", "[]", CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Equal($"forbidden capability: {name}", res.Error!.Message);
    }

    [Fact]
    public async Task Private_bridge_is_not_reachable()
    {
        using var engine = await Create("export function f() { return [typeof globalThis.__hostBridge, typeof globalThis.__native]; }");
        Assert.Equal("[\"undefined\",\"undefined\"]", (await Value(engine, "f"))!.ToJsonString());
    }

    [Fact]
    public async Task Time_limit_interrupts_a_runaway_loop()
    {
        using var engine = await Create("export function f() { for (;;) {} }", limits: new EngineLimits(TimeSpan.FromMilliseconds(200), 128L << 20));
        var res = await engine.CallAsync("f", "[]", CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Equal("time", res.Error!.Details!["limit"]!.GetValue<string>());
    }

    [Fact]
    public async Task SourceError_is_classified()
    {
        using var engine = await Create("""
            export function a() { throw new SourceError("RateLimited", "slow down", { retryAfterSeconds: 30 }); }
            export function b() { throw new TypeError("bad"); }
            export function c() { return new SourceError("NotFound", "x") instanceof Error; }
            """);
        var a = await engine.CallAsync("a", "[]", CancellationToken.None);
        Assert.Equal(("SourceError", "RateLimited", 30d), (a.Error!.Type, a.Error.Kind, a.Error.RetryAfterSeconds));
        var b = await engine.CallAsync("b", "[]", CancellationToken.None);
        Assert.Equal("ScriptError", b.Error!.Type);
        Assert.True((await Value(engine, "c"))!.GetValue<bool>());
    }

    [Fact]
    public async Task Storage_and_sleep_work()
    {
        using var engine = await Create("""
            export async function f() {
              await storage.set("k", "v");
              await sleep(20);
              return [await storage.get("k"), await storage.get("missing")];
            }
            """);
        Assert.Equal("[\"v\",null]", (await Value(engine, "f"))!.ToJsonString());
    }

    [Fact]
    public async Task Arguments_arrive_as_JSON()
    {
        using var engine = await Create("export function f(a, b) { return { a, b, n: arguments.length }; }");
        var v = await Value(engine, "f", JsonSerializer.Serialize(new object?[] { new { x = 1 }, null }));
        Assert.Equal("{\"a\":{\"x\":1},\"b\":null,\"n\":2}", v!.ToJsonString());
    }
}
