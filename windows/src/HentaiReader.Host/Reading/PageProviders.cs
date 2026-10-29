using System.Text.Json.Nodes;
using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Reading;

/// <summary>
/// Where the reader's page bytes come from (006 research R8): online through the source, or from a
/// saved archive. The strip, layout and load planning are the same for both.
/// </summary>
public interface IPageProvider : IDisposable
{
    GalleryKey Key { get; }
    string Title { get; }
    /// <summary>Known or estimated page sizes in reading order (0 × 0 = unknown, 2:3 is assumed).</summary>
    IReadOnlyList<(double Width, double Height)> Sizes { get; }
    /// <summary>Pages loaded at the same time.</summary>
    int Concurrency { get; }
    bool IsLimited { get; }
    void ClearLimit();

    /// <summary>
    /// Bytes of one page. <paramref name="onLocated"/> receives the exact size when it is learned before
    /// the download (online) — the caller applies it to the strip before anything is decoded.
    /// </summary>
    Task<byte[]> LoadAsync(int index, IPageAttempts page, Action<double?, double?>? onLocated, CancellationToken token);
}

/// <summary>005 online reading: page cache first, else getPageUrl + download through the source.</summary>
public sealed class OnlinePageProvider : IPageProvider
{
    private readonly SourceSession _session;
    private readonly IReadOnlyDictionary<string, string>? _settings;
    private readonly JsonObject _gallery;
    private readonly PageCache _cache;
    private readonly string _fingerprint;

    public OnlinePageProvider(SourceSession session, string sourceId, IReadOnlyDictionary<string, string>? settings, JsonObject gallery, PageCache cache)
    {
        _session = session;
        _settings = settings;
        _gallery = gallery;
        _cache = cache;
        _fingerprint = PageCache.Fingerprint(settings);
        Key = new GalleryKey(sourceId, gallery["id"]!.GetValue<string>());
        Title = gallery["title"]?.GetValue<string>() ?? "";
        Sizes = (gallery["pages"] as JsonArray ?? []).Select(p => (p?["width"]?.GetValue<double>() ?? 0, p?["height"]?.GetValue<double>() ?? 0)).ToList();
        // As many pages in flight as the source allows requests at once: more would only queue
        // later pages' requests in front of the first page's image (005 SC-001).
        Concurrency = Math.Clamp(session.Manifest["politeness"]?["maxConcurrency"]?.GetValue<int>() ?? 2, 1, 4);
    }

    public GalleryKey Key { get; }
    public string Title { get; }
    public IReadOnlyList<(double Width, double Height)> Sizes { get; }
    public int Concurrency { get; }
    public bool IsLimited => _session.State == SessionState.Limited;
    public JsonObject Gallery => _gallery;

    public void ClearLimit() => _session.ClearLimit();

    public async Task<byte[]> LoadAsync(int index, IPageAttempts page, Action<double?, double?>? onLocated, CancellationToken token)
    {
        var key = PageCache.Key(Key.SourceId, Key.GalleryId, index, _fingerprint);
        var cached = await Task.Run(() => _cache.TryGet(key, out var bytes) ? bytes : null, token);
        if (cached is not null) return cached;
        var loader = new PageImageLoader(_session, _settings);
        var data = await loader.LoadAsync(_gallery, index, page, image =>
            onLocated?.Invoke(image["width"]?.GetValue<double>(), image["height"]?.GetValue<double>()), token);
        await _cache.PutAsync(key, data, CancellationToken.None);
        return data;
    }

    public void Dispose()
    {
    }
}

/// <summary>A saved gallery: entries read straight from the archive, off the UI thread, no network.</summary>
public sealed class ArchivePageProvider : IPageProvider
{
    private readonly CbzReader _reader;

    /// <param name="pages">Page sizes from the library (metadata or measured); used when they match the archive.</param>
    public ArchivePageProvider(string path, GalleryKey key, string title, IReadOnlyList<ArchivePage>? pages)
    {
        _reader = CbzReader.Open(path);
        Key = key;
        Title = title;
        var byName = (pages ?? []).GroupBy(p => p.File).ToDictionary(g => g.Key, g => g.First());
        Sizes = _reader.Pages.Select(e => byName.TryGetValue(e.Name, out var p) ? ((double)p.Width, (double)p.Height) : (0d, 0d)).ToList();
    }

    public GalleryKey Key { get; }
    public string Title { get; }
    public IReadOnlyList<(double Width, double Height)> Sizes { get; }
    public int Concurrency => 3;
    public bool IsLimited => false;

    public void ClearLimit()
    {
    }

    public Task<byte[]> LoadAsync(int index, IPageAttempts page, Action<double?, double?>? onLocated, CancellationToken token) =>
        _reader.ReadEntryAsync(_reader.Pages[index], token);

    public void Dispose() => _reader.Dispose();
}
