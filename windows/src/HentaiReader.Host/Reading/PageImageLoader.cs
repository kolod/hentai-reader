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
/// usually comes from another one. The same happens after an HTTP error of the image server (with
/// the status in the hint and a growing pause). After <see cref="AutoRetries"/> such attempts the
/// error leaves the page to the user's Retry.
/// </summary>
public sealed class PageImageLoader(SourceSession session, IReadOnlyDictionary<string, string>? settings = null)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    public int AutoRetries { get; init; } = 2;
    /// <summary>Pause before the first retry after an HTTP error; doubled for each further one.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    private static bool Retryable(HttpRequestException e) =>
        e.StatusCode is null || (int)e.StatusCode >= 500 || (int)e.StatusCode is 403 or 404 or 408 or 410;

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
            catch (HttpRequestException e) when (Retryable(e) && page.Attempts < AutoRetries)
            {
                // A failing image server (5xx) or a location that no longer exists (404/410): ask the
                // source again with the status (Hitomi then refreshes its gg.js), after a growing pause
                // (Constitution VI). Limits (429/509) are not retried here; they stop the source.
                page.FailedStatus = (int?)e.StatusCode;
                page.Attempts++;
                await Task.Delay(RetryDelay * (1 << (page.Attempts - 1)), token);
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
