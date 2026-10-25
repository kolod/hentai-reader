using System.Text.Json.Nodes;
using HentaiReader.App.Services;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Reading;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace HentaiReader.App.Reader;

/// <summary>
/// Loads the pages of one gallery for the reader (specs/005-windows-reader research R4, R5, R7):
/// keeps the load window of <see cref="LoadPlanner"/> loaded and decoded, everything else released;
/// page images come from the disk cache or through the source (getPageUrl + source cookies and
/// limiter). Exact sizes from getPageUrl are applied to the geometry before the image is decoded.
/// Runs on the UI thread; awaited work (script calls, network, file reads) runs elsewhere.
/// </summary>
public sealed class ReaderSession : IDisposable
{
    /// <summary>Decoded images above this pixel count are decoded narrower (very tall pages).</summary>
    private const long MaxDecodedPixels = 32_000_000;

    private readonly SourceEntry _source;
    private readonly JsonObject _gallery;
    private readonly PageCache _cache;
    private readonly string _fingerprint;
    private readonly string _galleryId;
    private readonly Dictionary<int, CancellationTokenSource> _loading = new();
    private readonly SemaphoreSlim _slots;
    private LoadPlan _plan = LoadPlan.Empty;
    private bool _disposed;

    public ReaderSession(SourceEntry source, JsonObject gallery, PageCache cache, double columnWidth)
    {
        _source = source;
        _gallery = gallery;
        _cache = cache;
        _galleryId = gallery["id"]!.GetValue<string>();
        _fingerprint = PageCache.Fingerprint(source.Settings?.Values);
        // As many pages in flight as the source allows requests at once: more would only queue
        // later pages' requests in front of the first page's image (SC-001).
        var concurrency = source.Session?.Manifest["politeness"]?["maxConcurrency"]?.GetValue<int>() ?? 2;
        _slots = new SemaphoreSlim(Math.Clamp(concurrency, 1, 4));
        var pages = gallery["pages"] as JsonArray ?? [];
        var sizes = pages.Select(p => (p?["width"]?.GetValue<double>() ?? 0, p?["height"]?.GetValue<double>() ?? 0)).ToList();
        Geometry = new StripGeometry(sizes, columnWidth);
        Pages = Enumerable.Range(0, sizes.Count).Select(i => new PageState(i, sizes.Count)).ToList();
    }

    public StripGeometry Geometry { get; }
    public IReadOnlyList<PageState> Pages { get; }
    public string Title => _gallery["title"]?.GetValue<string>() ?? "";
    public string SourceId => _source.Id;
    public string GalleryId => _galleryId;

    /// <summary>Pixels per DIP for decoding (window rasterization scale).</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Pages to keep ahead (preload setting, 3–5).</summary>
    public int PreloadPages { get; set; } = 4;

    /// <summary>A page's height changed (exact size learned); args: page, height delta.</summary>
    public event Action<int, double>? PageSizeChanged;

    /// <summary>The source signalled a limit; nothing is requested until <see cref="RetryAfterLimit"/>.</summary>
    public event Action<double?>? Limited;

    public bool IsLimited => _source.Session?.State == SessionState.Limited;

    /// <summary>Re-plans for the current view: starts loads in priority order, releases the rest.</summary>
    public void UpdateView(double scrollY, double viewportHeight)
    {
        if (_disposed) return;
        _plan = LoadPlanner.Plan(Geometry, scrollY, viewportHeight, PreloadPages);

        foreach (var (page, cts) in _loading.Where(l => !_plan.Contains(l.Key)).ToList())
        {
            cts.Cancel();
            _loading.Remove(page);
            if (Pages[page].Load is not PageLoad.Failed) Pages[page].Load = PageLoad.Placeholder;
        }
        foreach (var page in Pages.Where(p => p.Image is not null && !_plan.Contains(p.Index)))
        {
            Release(page);
        }
        if (IsLimited) return;
        foreach (var index in _plan.Ordered)
        {
            var page = Pages[index];
            if (page.Load is PageLoad.Placeholder && !_loading.ContainsKey(index)) Start(page);
        }
    }

    /// <summary>The column width changed (window, reading width, zoom): images are re-decoded at the new width.</summary>
    public void SetColumnWidth(double columnWidth)
    {
        Geometry.SetColumnWidth(columnWidth);
        foreach (var page in Pages.Where(p => p.Image is not null && Math.Abs(p.DecodedWidth - DecodeWidth(p.Index)) > p.DecodedWidth * 0.2))
        {
            Release(page);
        }
    }

    /// <summary>Retry of one failed page: the source is asked for a new location with a RetryHint.</summary>
    public void Retry(PageState page)
    {
        if (page.Load is not PageLoad.Failed) return;
        page.Error = null;
        page.Load = PageLoad.Placeholder;
        if (_plan.Contains(page.Index) && !IsLimited) Start(page);
    }

    /// <summary>The user chose Retry after a limit: requests resume (FR-021).</summary>
    public void RetryAfterLimit()
    {
        _source.Session?.ClearLimit();
        foreach (var page in Pages.Where(p => p.Load is PageLoad.Failed)) page.Load = PageLoad.Placeholder;
    }

    private void Start(PageState page)
    {
        var cts = new CancellationTokenSource();
        _loading[page.Index] = cts;
        _ = LoadAsync(page, cts.Token);
    }

    private async Task LoadAsync(PageState page, CancellationToken token)
    {
        await _slots.WaitAsync(token).ConfigureAwait(true);
        try
        {
            var key = PageCache.Key(_source.Id, _galleryId, page.Index, _fingerprint);
            var bytes = await Task.Run(() => _cache.TryGet(key, out var cached) ? cached : null, token);
            if (bytes is null)
            {
                page.Load = PageLoad.Resolving;
                var loader = new PageImageLoader(_source.Session!, _source.Settings?.Values);
                bytes = await loader.LoadAsync(_gallery, page.Index, page, image =>
                {
                    if (image["width"] is JsonValue w && image["height"] is JsonValue h) ApplySize(page.Index, w.GetValue<double>(), h.GetValue<double>());
                    page.Load = PageLoad.Downloading;
                }, token);
                await _cache.PutAsync(key, bytes, CancellationToken.None);
            }
            token.ThrowIfCancellationRequested();
            page.Load = PageLoad.Decoding;
            await DecodeAsync(page, bytes, token);
            page.Attempts = 0;
            page.FailedStatus = null;
            page.Load = PageLoad.Loaded;
        }
        catch (OperationCanceledException)
        {
            // Left the load window; the page goes back to Placeholder (UpdateView).
        }
        catch (PageResolveException e)
        {
            Fail(page, e.Error.Type == "SourceError" ? e.Error.Kind! : "ScriptError");
            if (e.Error.Kind == SourceErrorKind.RateLimited) Limited?.Invoke(e.Error.RetryAfterSeconds);
        }
        catch (SourceLimitedException e)
        {
            Fail(page, "RateLimited");
            Limited?.Invoke(e.RetryAfterSeconds);
        }
        catch (HttpRequestException e)
        {
            page.FailedStatus = (int?)e.StatusCode;
            Fail(page, "Network");
        }
        catch (Exception e) when (e is TimeoutException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Fail(page, "Network");
        }
        finally
        {
            _slots.Release();
            if (_loading.TryGetValue(page.Index, out var cts) && cts.Token == token) _loading.Remove(page.Index);
        }
    }

    private async Task DecodeAsync(PageState page, byte[] bytes, CancellationToken token)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var width = DecodeWidth(page.Index);
        var bitmap = new BitmapImage { DecodePixelWidth = width, DecodePixelType = DecodePixelType.Physical };
        await bitmap.SetSourceAsync(stream);
        token.ThrowIfCancellationRequested();
        // The image itself is the final word on proportions (cached pages never saw a PageImage size).
        if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0) ApplySize(page.Index, bitmap.PixelWidth, bitmap.PixelHeight);
        page.DecodedWidth = width;
        page.Image = bitmap;
    }

    private void ApplySize(int index, double width, double height)
    {
        var before = Geometry.HeightOf(index);
        var expected = Geometry.ColumnWidth * height / width;
        if (Math.Abs(expected - before) < 0.5) return;
        var delta = Geometry.SetPageSize(index, width, height);
        PageSizeChanged?.Invoke(index, delta);
    }

    /// <summary>Physical decode width for the current column; narrower for extremely tall pages.</summary>
    private int DecodeWidth(int index)
    {
        var width = Geometry.ColumnWidth * Scale;
        var height = Geometry.HeightOf(index) * Scale;
        if (width * height > MaxDecodedPixels) width *= Math.Sqrt(MaxDecodedPixels / (width * height));
        return Math.Max(1, (int)Math.Round(width));
    }

    private void Fail(PageState page, string kind)
    {
        page.Attempts++;
        page.Error = kind;
        page.Load = PageLoad.Failed;
    }

    private static void Release(PageState page)
    {
        page.Image = null;
        page.DecodedWidth = 0;
        if (page.Load is PageLoad.Loaded or PageLoad.Decoding) page.Load = PageLoad.Placeholder;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var cts in _loading.Values) cts.Cancel();
        _loading.Clear();
        foreach (var page in Pages) Release(page);
    }
}
