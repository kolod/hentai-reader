using System.Net;

namespace HentaiReader.Host.Http;

/// <summary>Real network. Cookies and redirects are handled by HttpHost, never by HttpClient.</summary>
public sealed class LiveTransport : ITransport, IDisposable
{
    private readonly HttpClient _client = Create(DecompressionMethods.All);

    // Byte ranges must address the identity representation: asked with Accept-Encoding, nginx
    // (Hitomi) answers a range of the gzip file, which then inflates to a truncated body. Without
    // automatic decompression HttpClient sends no Accept-Encoding at all.
    private readonly HttpClient _identity = Create(DecompressionMethods.None);

    private static HttpClient Create(DecompressionMethods decompression) => new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = decompression,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timeout);
        var client = request.Headers.ContainsKey("range") ? _identity : _client;
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null) message.Content = new ByteArrayContent(request.Body);
        foreach (var (name, value) in request.Headers)
        {
            if (name.StartsWith("content-", StringComparison.Ordinal))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (!message.Headers.UserAgent.Any()) message.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HentaiReader");

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in response.Headers.Concat(response.Content.Headers))
        {
            foreach (var v in h.Value) headers.Add(new(h.Key.ToLowerInvariant(), v));
        }
        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        return new RawResponse((int)response.StatusCode, headers, body);
    }

    public void Dispose()
    {
        _client.Dispose();
        _identity.Dispose();
    }
}
