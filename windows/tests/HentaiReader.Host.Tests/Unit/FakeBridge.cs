using System.Text.Json;
using HentaiReader.Host.Engine;

namespace HentaiReader.Host.Tests.Unit;

/// <summary>Scriptable bridge for engine tests: HTTP answered by a delegate, storage in memory.</summary>
internal sealed class FakeBridge : IHostBridge
{
    private readonly Dictionary<string, string> _storage = new();
    public Func<string, byte[]?, Task<HttpBridgeResult>> Http { get; set; } =
        (_, _) => Task.FromResult(new HttpBridgeResult(Envelope.Value(new { status = 200, url = "https://a.test/", headers = new { }, text = "ok" }), null));
    public List<string> Logs { get; } = new();
    public Exception? Fatal { get; set; }

    public Task<HttpBridgeResult> HttpAsync(string metaJson, byte[]? body) => Http(metaJson, body);
    public string HtmlSelect(string markup, int elementId, string selector) => Envelope.Value(Array.Empty<object>());
    public string HtmlCall(string op, int elementId, string arg) => Envelope.Value(null);

    public Task<string> StorageGetAsync(string key) =>
        Task.FromResult(Envelope.Value(_storage.TryGetValue(key, out var v) ? v : null));

    public Task<string> StorageSetAsync(string key, string valueJson)
    {
        var value = JsonSerializer.Deserialize<string?>(valueJson);
        if (value is null) _storage.Remove(key); else _storage[key] = value;
        return Task.FromResult(Envelope.Value(null));
    }

    public async Task<string> SleepAsync(double ms)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(ms));
        return Envelope.Value(null);
    }

    public void Log(string level, string text) => Logs.Add($"{level}: {text}");
    public void Idle() { }

    public static HttpBridgeResult Text(string url, string text, int status = 200) =>
        new(Envelope.Value(new { status, url, headers = new { }, text }), null);

    public static HttpBridgeResult Bytes(string url, byte[] bytes) =>
        new(Envelope.Value(new { status = 200, url, headers = new { }, bytes = true }), bytes);

    public static string Manifest(string contract = "1.0") =>
        $$"""export const manifest = { id: "t", name: "T", version: "0.0.1", contract: "{{contract}}", domains: ["a.test"], politeness: { minIntervalMs: 0, maxConcurrency: 4 }, login: "none", filters: [], capabilities: { suggestTags: false } };""";
}
