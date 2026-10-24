using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HentaiReader.Host.Http;

namespace HentaiReader.Host.Fixtures;

/// <summary>
/// Answers requests only from a recorded fixture pool (port of tools/runner/src/transport/offline.ts):
/// key = method + URL + range header + SHA-256 of the body; supports echo and delayMs.
/// </summary>
public sealed class OfflineTransport : ITransport
{
    private readonly string _dir;
    private readonly Dictionary<string, JsonObject> _entries = new();

    public long RecordedAt { get; }

    public OfflineTransport(string fixturesDir)
    {
        _dir = fixturesDir;
        var index = Path.Combine(fixturesDir, "index.json");
        if (!File.Exists(index))
        {
            RecordedAt = 0;
            return;
        }
        var root = JsonNode.Parse(File.ReadAllText(index))!.AsObject();
        RecordedAt = DateTimeOffset.Parse(root["recordedAt"]!.GetValue<string>()).ToUnixTimeMilliseconds();
        foreach (var e in root["entries"]!.AsArray())
        {
            var entry = e!.AsObject();
            var req = entry["request"]!.AsObject();
            _entries[Key(req["method"]!.GetValue<string>(), req["url"]!.GetValue<string>(),
                req["headers"]?["range"]?.GetValue<string>(), req["bodySha256"]?.GetValue<string>())] = entry;
        }
    }

    public static string Key(string method, string url, string? range, string? bodySha256) =>
        string.Join('\n', method.ToUpperInvariant(), url, range ?? "", bodySha256 ?? "");

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public async Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default)
    {
        var bodySha = request.Body is null ? null : Sha256Hex(request.Body);
        request.Headers.TryGetValue("range", out var range);
        if (!_entries.TryGetValue(Key(request.Method, request.Url, range, bodySha), out var entry))
            throw new UnmatchedRequestException(request.Method, request.Url, range);
        var response = entry["response"]!.AsObject();
        if (response["delayMs"] is JsonValue d && d.GetValue<double>() > 0) await Task.Delay(TimeSpan.FromMilliseconds(d.GetValue<double>()), token);

        if (response["echo"] is JsonValue echo && echo.GetValue<bool>())
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["method"] = request.Method, ["url"] = request.Url, ["headers"] = request.Headers, ["bodySha256"] = bodySha,
            });
            return new RawResponse(200, [new("content-type", "application/json")], Encoding.UTF8.GetBytes(json));
        }
        var headers = new List<KeyValuePair<string, string>>();
        if (response["headers"] is JsonObject h)
        {
            foreach (var (k, v) in h) headers.Add(new(k.ToLowerInvariant(), v!.GetValue<string>()));
        }
        var bodyPath = response["body"]?.GetValue<string>();
        var body = bodyPath is null ? [] : await File.ReadAllBytesAsync(Path.Combine(_dir, bodyPath), token);
        return new RawResponse(response["status"]!.GetValue<int>(), headers, body);
    }
}
