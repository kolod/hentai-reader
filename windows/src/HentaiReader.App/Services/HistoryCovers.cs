using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;
using HentaiReader.Host.Reading;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HentaiReader.App.Services;

/// <summary>
/// Small cover thumbnails of history entries in <c>covers\</c> (006 research R11), made once from the
/// first page the reader already loaded. A missing one is made from what is on disk; History asks the
/// source only when the user allowed it (<see cref="Preferences.NetworkCovers"/>).
/// </summary>
public static class HistoryCovers
{
    public const int Width = 96;

    public static string FileName(GalleryKey key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..24].ToLowerInvariant() + ".jpg";

    public static string PathOf(string file) => System.IO.Path.Combine(AppPaths.Covers, file);

    /// <summary>Whether the entry's cover file exists (it may be missing: made before covers existed, or deleted).</summary>
    public static bool Has(HistoryEntry entry) => entry.CoverFile is { } file && File.Exists(PathOf(file));

    /// <summary>
    /// A cover for an entry that has none: the saved copy's first page, else the cached first page,
    /// else — only with <paramref name="network"/> — through the source: getGallery, then its cover
    /// image (the first page when the source gives no cover). Returns the cover file, or null.
    /// </summary>
    public static async Task<string?> FindAsync(HistoryEntry entry, bool network, CancellationToken token)
    {
        var key = entry.Key;
        var library = await AppServices.Library;
        if (await library.FindSavedAsync(key, token) is { } saved)
        {
            var bytes = await Task.Run(() => FirstPage(saved.FullPath), token);
            if (bytes is not null && await SaveAsync(key, bytes) is { } file) return file;
        }
        var source = MainWindow.Instance?.FindSource(key.SourceId);
        var values = source?.Settings?.Values;
        var cacheKey = PageCache.Key(key.SourceId, key.GalleryId, 0, PageCache.Fingerprint(values));
        var cached = await Task.Run(() => AppServices.PageCache.TryGet(cacheKey, out var b) ? b : null, token);
        if (cached is not null && await SaveAsync(key, cached) is { } fromCache) return fromCache;

        if (!network || source is not { IsAvailable: true, Session: { State: not SessionState.Limited } session }) return null;
        var result = await source.CallAsync("getGallery", [JsonValue.Create(key.GalleryId)]);
        token.ThrowIfCancellationRequested();
        if (!result.Ok || result.Value is not JsonObject gallery) return null;
        byte[] image;
        if (gallery["cover"] is JsonObject cover && cover["url"]?.GetValue<string>() is { } url && cover["crop"] is null)
        {
            var headers = (cover["headers"] as JsonObject ?? []).ToDictionary(h => h.Key, h => h.Value!.GetValue<string>());
            image = await session.Http.FetchImageAsync(url, headers, token);
        }
        else
        {
            using var provider = new OnlinePageProvider(session, key.SourceId, values, gallery, AppServices.PageCache);
            image = await provider.LoadAsync(0, new Reader.PageState(0, Math.Max(1, provider.Sizes.Count)), null, token);
        }
        return await SaveAsync(key, image);
    }

    private static byte[]? FirstPage(string archive)
    {
        try
        {
            using var reader = CbzReader.Open(archive);
            return reader.Pages.Count > 0 ? reader.ReadEntry(reader.Pages[0]) : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the thumbnail and records it in history; failures are ignored (the entry just has no cover). Returns the file or null.</summary>
    public static async Task<string?> SaveAsync(GalleryKey key, byte[] pageBytes)
    {
        try
        {
            var file = FileName(key);
            var path = PathOf(file);
            if (!File.Exists(path))
            {
                using var input = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(pageBytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                var decoder = await BitmapDecoder.CreateAsync(input);
                var height = (uint)Math.Max(1, Math.Round(Width * (double)decoder.PixelHeight / Math.Max(1, decoder.PixelWidth)));
                var transform = new BitmapTransform { ScaledWidth = Width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                using var output = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, Width, height, 96, 96, pixels.DetachPixelData());
                await encoder.FlushAsync();
                Directory.CreateDirectory(AppPaths.Covers);
                var bytes = new byte[output.Size];
                using (var reader = new DataReader(output.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(bytes);
                }
                await File.WriteAllBytesAsync(path, bytes);
            }
            await (await AppServices.Library).History.SetCoverAsync(key, file);
            return file;
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string? file)
    {
        if (file is null) return;
        try { File.Delete(PathOf(file)); } catch (IOException) { }
    }
}
