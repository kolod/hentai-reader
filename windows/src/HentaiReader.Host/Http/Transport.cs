namespace HentaiReader.Host.Http;

/// <summary>A request exactly as sent: lower-case header names (including cookies).</summary>
public sealed record RawRequest(string Method, string Url, Dictionary<string, string> Headers, byte[]? Body, TimeSpan Timeout);

/// <summary>Response as received: lower-case header names, every set-cookie kept.</summary>
public sealed record RawResponse(int Status, List<KeyValuePair<string, string>> Headers, byte[] Body);

public interface ITransport
{
    Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default);
}

/// <summary>A transport failure that aborts the whole call, whatever the script does.</summary>
public class FatalTransportException(string message) : Exception(message);

/// <summary>Offline replay received a request with no recorded fixture.</summary>
public sealed class UnmatchedRequestException(string method, string url, string? range)
    : FatalTransportException($"no recorded fixture for {method} {url}{(range is null ? "" : $" (range: {range})")}");
