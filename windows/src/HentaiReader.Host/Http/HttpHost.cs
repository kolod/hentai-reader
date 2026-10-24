using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HentaiReader.Host.Engine;

namespace HentaiReader.Host.Http;

public sealed record HttpManifest(IReadOnlyList<string> Domains, Politeness Politeness);

public sealed class RequestLogEntry
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public long StartedAt { get; init; }
    public long? EndedAt { get; set; }
    public int? Status { get; set; }
    public string? Refused { get; init; }
}

/// <summary>
/// http.request for the Windows host: validation, domain allow-list, politeness, cookie jar,
/// redirects, response shaping (port of tools/runner/src/host/http.ts). The same pipeline serves
/// image requests (contract 1.1: images use the source's cookies and limiter).
/// </summary>
public sealed class HttpHost
{
    static HttpHost() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly HashSet<int> Redirects = [301, 302, 303, 307, 308];
    private const int MaxRedirects = 5;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ITransport _transport;
    private readonly bool _enforcePoliteness;
    private readonly Scheduler _scheduler;
    private HttpManifest? _manifest;

    public CookieJar Cookies { get; }
    public List<RequestLogEntry> Log { get; } = new();
    public Action<Exception>? OnFatal { get; set; }

    public HttpHost(ITransport transport, bool enforcePoliteness, CookieJar? cookies = null)
    {
        _transport = transport;
        _enforcePoliteness = enforcePoliteness;
        Cookies = cookies ?? new CookieJar();
        _scheduler = new Scheduler(() => _enforcePoliteness ? _manifest?.Politeness : null);
    }

    public void SetManifest(HttpManifest manifest) => _manifest = manifest;

    public bool IsAllowed(string url)
    {
        if (_manifest is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return _manifest.Domains.Any(d =>
        {
            var pattern = d.ToLowerInvariant();
            return pattern.StartsWith("*.", StringComparison.Ordinal)
                ? CookieJar.DomainMatches(host, pattern[2..]) && host != pattern[2..]
                : host == pattern;
        });
    }

    private sealed class HttpFailure(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    /// <summary>Bridge entry point: JSON request metadata (+ byte body) → envelope (+ bytes).</summary>
    public async Task<HttpBridgeResult> RequestAsync(string metaJson, byte[]? bodyBytes)
    {
        try
        {
            var (raw, responseType) = Validate(JsonNode.Parse(metaJson)!.AsObject(), bodyBytes);
            var (url, response) = await PerformAsync(raw);
            var headers = ShapeHeaders(response);
            if (responseType == "bytes")
            {
                return new HttpBridgeResult(Envelope.Value(new Dictionary<string, object?>
                {
                    ["status"] = response.Status, ["url"] = url, ["headers"] = headers, ["bytes"] = true,
                }), response.Body);
            }
            headers.TryGetValue("content-type", out var contentType);
            return new HttpBridgeResult(Envelope.Value(new Dictionary<string, object?>
            {
                ["status"] = response.Status, ["url"] = url, ["headers"] = headers, ["text"] = Decode(response.Body, contentType),
            }), null);
        }
        catch (HttpFailure e)
        {
            return new HttpBridgeResult(Envelope.Error(e.Kind, e.Message), null);
        }
        catch (FatalTransportException e)
        {
            OnFatal?.Invoke(e);
            return new HttpBridgeResult(Envelope.Error(SourceErrorKind.Network, e.Message), null);
        }
        catch (Exception e)
        {
            return new HttpBridgeResult(Envelope.Error(SourceErrorKind.Network, e.Message), null);
        }
    }

    /// <summary>Fetches an image for the UI through the same domain check, cookies and limiter.</summary>
    public async Task<byte[]> FetchImageAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken token = default)
    {
        var raw = new RawRequest("GET", url, headers?.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value) ?? new(), null, DefaultTimeout);
        var (_, response) = await PerformAsync(raw, token);
        if (response.Status is < 200 or >= 300) throw new HttpRequestException($"HTTP {response.Status} for {url}");
        return response.Body;
    }

    private static (RawRequest, string) Validate(JsonObject meta, byte[]? bodyBytes)
    {
        var method = meta["method"]?.GetValue<string>() ?? "GET";
        if (method is not ("GET" or "POST")) throw new HttpFailure(SourceErrorKind.InvalidInput, $"http.request: unsupported method {method}");
        var url = meta["url"] is JsonValue u && u.TryGetValue<string>(out var s) ? s : null;
        if (url is null || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new HttpFailure(SourceErrorKind.InvalidInput, $"http.request: absolute http(s) URL required, got {url ?? "undefined"}");
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new HttpFailure(SourceErrorKind.InvalidInput, $"http.request: invalid URL {url}");

        var headers = new Dictionary<string, string>();
        if (meta["headers"] is JsonObject h)
        {
            foreach (var (k, v) in h)
            {
                if (v is not JsonValue jv || !jv.TryGetValue<string>(out var str))
                    throw new HttpFailure(SourceErrorKind.InvalidInput, $"http.request: header {k} must be a string");
                headers[k.ToLowerInvariant()] = str;
            }
        }
        var body = bodyBytes;
        if (meta["body"] is JsonValue b && b.TryGetValue<string>(out var text)) body = Encoding.UTF8.GetBytes(text);
        else if (meta["body"] is not null) throw new HttpFailure(SourceErrorKind.InvalidInput, "http.request: body must be a string or Uint8Array");
        if (body is not null && method != "POST") throw new HttpFailure(SourceErrorKind.InvalidInput, "http.request: body is only allowed with POST");

        var responseType = meta["responseType"]?.GetValue<string>() ?? "text";
        if (responseType is not ("text" or "bytes")) throw new HttpFailure(SourceErrorKind.InvalidInput, $"http.request: unsupported responseType {responseType}");
        var timeout = meta["timeoutMs"] is JsonValue t && t.TryGetValue<double>(out var ms)
            ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 1, 120_000))
            : DefaultTimeout;
        return (new RawRequest(method, url, headers, body, timeout), responseType);
    }

    private async Task<(string Url, RawResponse Response)> PerformAsync(RawRequest first, CancellationToken token = default)
    {
        var req = first;
        for (var hop = 0; ; hop++)
        {
            if (!IsAllowed(req.Url))
            {
                lock (Log) Log.Add(new RequestLogEntry { Method = req.Method, Url = req.Url, Headers = req.Headers, StartedAt = Now(), Refused = "domain" });
                throw new HttpFailure(SourceErrorKind.InvalidInput, $"http.request: domain not declared in manifest: {new Uri(req.Url).Host}");
            }
            var headers = new Dictionary<string, string>(req.Headers);
            req.Headers.TryGetValue("cookie", out var scriptCookie);
            var cookie = CookieJar.MergeCookieHeader(Cookies.For(req.Url), scriptCookie);
            if (cookie is not null) headers["cookie"] = cookie; else headers.Remove("cookie");
            var sent = req with { Headers = headers };

            await _scheduler.AcquireAsync(token);
            var entry = new RequestLogEntry { Method = sent.Method, Url = sent.Url, Headers = sent.Headers, StartedAt = Now() };
            lock (Log) Log.Add(entry);
            RawResponse response;
            try
            {
                response = await _transport.SendAsync(sent, token);
            }
            finally
            {
                entry.EndedAt = Now();
                _scheduler.Release();
            }
            entry.Status = response.Status;
            Cookies.Store(req.Url, response.Headers.Where(h => h.Key == "set-cookie").Select(h => h.Value));

            var location = response.Headers.FirstOrDefault(h => h.Key == "location").Value;
            if (!Redirects.Contains(response.Status) || location is null) return (req.Url, response);
            if (hop >= MaxRedirects) throw new HttpFailure(SourceErrorKind.Network, $"http.request: more than {MaxRedirects} redirects");
            var toGet = response.Status == 303 || (response.Status is 301 or 302 && req.Method == "POST");
            req = req with
            {
                Url = new Uri(new Uri(req.Url), location).AbsoluteUri,
                Method = toGet ? "GET" : req.Method,
                Body = toGet ? null : req.Body,
            };
        }
    }

    private static Dictionary<string, string> ShapeHeaders(RawResponse response)
    {
        var headers = new Dictionary<string, string>();
        foreach (var (name, value) in response.Headers)
        {
            if (name == "set-cookie") continue;
            headers[name] = headers.TryGetValue(name, out var existing) ? $"{existing}, {value}" : value;
        }
        return headers;
    }

    private static string Decode(byte[] body, string? contentType)
    {
        var charset = contentType is null ? null : System.Text.RegularExpressions.Regex.Match(contentType, "charset=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups[1].Value.Trim().Trim('"')
            : null;
        try
        {
            return (charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset)).GetString(body);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(body);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
