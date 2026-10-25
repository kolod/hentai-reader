using System.Text.Json.Nodes;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Sources;

namespace HentaiReader.Host.Reading;

/// <summary>What a page remembers between attempts; the next RetryHint is built from it.</summary>
public interface IPageAttempts
{
    /// <summary>Last PageImage from the source (RetryHint.previous).</summary>
    JsonNode? PageImage { get; set; }
    /// <summary>Failed attempts so far (RetryHint.attempt of the next try).</summary>
    int Attempts { get; set; }
    /// <summary>HTTP status of the last failed image fetch; null for network failures and timeouts.</summary>
    int? FailedStatus { get; set; }
}

/// <summary>getPageUrl failed; <see cref="Error"/> is the source's classified error.</summary>
public sealed class PageResolveException(CallError error) : Exception(error.Message)
{
    public CallError Error { get; } = error;
}

/// <summary>
/// Resolves a page through the source (getPageUrl, with a RetryHint after failures) and downloads
/// its image. A download that takes longer than <see cref="Timeout"/> is abandoned and the source
/// is asked again: a stalled image server (E-Hentai's H@H) rarely recovers, while a new location
/// usually comes from another one. After <see cref="AutoRetries"/> such attempts a
/// <see cref="TimeoutException"/> leaves the page to the user's Retry.
/// </summary>
public sealed class PageImageLoader(SourceSession session, IReadOnlyDictionary<string, string>? settings = null)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    public int AutoRetries { get; init; } = 2;

    /// <param name="onResolved">Called with every PageImage before its download (exact sizes, state).</param>
    public async Task<byte[]> LoadAsync(JsonObject gallery, int index, IPageAttempts page, Action<JsonNode>? onResolved, CancellationToken token)
    {
        while (true)
        {
            var result = await session.CallAsync("getPageUrl", [gallery.DeepClone(), index, RetryHint(page)], settings, token);
            token.ThrowIfCancellationRequested();
            if (!result.Ok) throw new PageResolveException(result.Error!);
            var image = result.Value!;
            page.PageImage = image;
            onResolved?.Invoke(image);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Timeout);
            try
            {
                return await session.FetchImageAsync(image, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                page.FailedStatus = null;
                if (page.Attempts >= AutoRetries) throw new TimeoutException($"page {index + 1}: image did not arrive in {Timeout.TotalSeconds:0} s");
                page.Attempts++;
            }
        }
    }

    /// <summary>RetryHint for the next getPageUrl, or null on the first attempt.</summary>
    public static JsonObject? RetryHint(IPageAttempts page)
    {
        if (page.Attempts == 0 || page.PageImage is null) return null;
        var hint = new JsonObject { ["attempt"] = page.Attempts, ["previous"] = page.PageImage.DeepClone() };
        if (page.FailedStatus is { } status) hint["status"] = status;
        return hint;
    }
}
