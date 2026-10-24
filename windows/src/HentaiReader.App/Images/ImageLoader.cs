using System.Text.Json.Nodes;
using HentaiReader.Host.Sources;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace HentaiReader.App.Images;

/// <summary>
/// Loads covers and thumbnails through the source session (its cookies, required headers, domain
/// check and politeness limiter — contract 1.1), decodes at display size off the UI thread, and
/// keeps an in-memory LRU of decoded images (~150 MB). Must be used from the UI thread.
/// </summary>
public sealed class ImageLoader
{
    private const long BudgetBytes = 150L * 1024 * 1024;
    private readonly LinkedList<(string Key, BitmapImage Image, long Bytes)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image, long Bytes)>> _index = new();
    private readonly Dictionary<string, Task<BitmapImage?>> _loading = new();
    private long _bytes;

    public static ImageLoader Shared { get; } = new();

    /// <param name="request">An ImageRequest from the source ({url, headers, crop?}).</param>
    /// <param name="decodeWidth">Pixel width to decode to; 0 decodes at full size (sprites).</param>
    public Task<BitmapImage?> LoadAsync(SourceSession session, JsonNode? request, int decodeWidth, CancellationToken token)
    {
        var url = request?["url"]?.GetValue<string>();
        if (url is null) return Task.FromResult<BitmapImage?>(null);
        var key = $"{decodeWidth}|{url}";
        if (_index.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return Task.FromResult<BitmapImage?>(node.Value.Image);
        }
        if (_loading.TryGetValue(key, out var pending)) return pending;
        var task = LoadCoreAsync(session, url, Headers(request), decodeWidth, key, token);
        _loading[key] = task;
        return task;
    }

    private async Task<BitmapImage?> LoadCoreAsync(SourceSession session, string url, Dictionary<string, string> headers, int decodeWidth, string key, CancellationToken token)
    {
        try
        {
            var bytes = await session.Http.FetchImageAsync(url, headers, token);
            var image = new BitmapImage();
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            Remember(key, image, Math.Max(1, image.PixelWidth) * (long)Math.Max(1, image.PixelHeight) * 4);
            return image;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _loading.Remove(key);
        }
    }

    private void Remember(string key, BitmapImage image, long bytes)
    {
        _index[key] = _lru.AddFirst((key, image, bytes));
        _bytes += bytes;
        while (_bytes > BudgetBytes && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
        }
    }

    private static Dictionary<string, string> Headers(JsonNode? request)
    {
        var result = new Dictionary<string, string>();
        if (request?["headers"] is JsonObject h)
        {
            foreach (var (k, v) in h) result[k] = v!.GetValue<string>();
        }
        return result;
    }
}
