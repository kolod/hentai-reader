using HentaiReader.Host.Archive;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace HentaiReader.App.Images;

/// <summary>
/// Library covers: the first page of a saved archive decoded at card size (006 research R11), read off
/// the UI thread, kept in a small LRU. Must be used from the UI thread.
/// </summary>
public sealed class ArchiveCovers
{
    private const int Capacity = 400;
    private readonly LinkedList<(string Key, BitmapImage Image)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> _index = new();

    public static ArchiveCovers Shared { get; } = new();

    public async Task<BitmapImage?> LoadAsync(string path, long size, int decodeWidth, CancellationToken token)
    {
        var key = $"{path}|{size}|{decodeWidth}";
        if (_index.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
        byte[]? bytes;
        try
        {
            bytes = await Task.Run(() =>
            {
                using var reader = CbzReader.Open(path);
                return reader.Pages.Count > 0 ? reader.ReadEntry(reader.Pages[0]) : null;
            }, token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
        if (bytes is null || token.IsCancellationRequested) return null;
        var image = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Physical };
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        try
        {
            await image.SetSourceAsync(stream);
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        _index[key] = _lru.AddFirst((key, image));
        while (_lru.Count > Capacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
        }
        return image;
    }
}
