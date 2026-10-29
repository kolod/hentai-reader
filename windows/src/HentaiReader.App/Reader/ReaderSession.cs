using System.Text.Json.Nodes;
using HentaiReader.Host.Archive;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Reading;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace HentaiReader.App.Reader;

/// <summary>
/// Loads the pages of one gallery for the reader (specs/005-windows-reader research R4, R5, R7):
/// keeps the load window of <see cref="LoadPlanner"/> loaded and decoded, everything else released;
/// page bytes come from an <see cref="IPageProvider"/> (online through the source, or a saved
/// archive). Exact sizes learned before a download are applied to the geometry before decoding.
/// Runs on the UI thread; awaited work (script calls, network, file reads) runs elsewhere.
/// </summary>
public sealed class ReaderSession : IDisposable
{
    /// <summary>Decoded images above this pixel count are decoded narrower (very tall pages).</summary>
    private const long MaxDecodedPixels = 32_000_000;

    /// <summary>Largest texture side of Direct3D feature level 11; a taller decode may not show.</summary>
    private const double MaxTextureSize = 16384;

    /// <summary>A decode taking longer than this is treated as stalled.</summary>
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(15);

    private readonly IPageProvider _provider;
    private readonly Dictionary<int, CancellationTokenSource> _loading = new();
    private readonly SemaphoreSlim _slots;
    private LoadPlan _plan = LoadPlan.Empty;
    private bool _disposed;

    public ReaderSession(IPageProvider provider, double columnWidth)
    {
        _provider = provider;
        _slots = new SemaphoreSlim(provider.Concurrency);
        var sizes = provider.Sizes;
        Geometry = new StripGeometry(sizes, columnWidth);
        Pages = Enumerable.Range(0, sizes.Count).Select(i => new PageState(i, sizes.Count)).ToList();
    }

    public StripGeometry Geometry { get; }
    public IReadOnlyList<PageState> Pages { get; }
    public string Title => _provider.Title;
    public GalleryKey Key => _provider.Key;
    public IPageProvider Provider => _provider;

    /// <summary>Pixels per DIP for decoding (window rasterization scale).</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Pages to keep ahead (preload setting, 3–5).</summary>
    public int PreloadPages { get; set; } = 4;

    /// <summary>A page's height changed (exact size learned); args: page, height delta.</summary>
    public event Action<int, double>? PageSizeChanged;

    /// <summary>A page's bytes arrived (before decoding); args: page, bytes.</summary>
    public event Action<int, byte[]>? PageBytes;

    /// <summary>The source signalled a limit; nothing is requested until <see cref="RetryAfterLimit"/>.</summary>
    public event Action<double?>? Limited;

    public bool IsLimited => _provider.IsLimited;

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
        _provider.ClearLimit();
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
        // A slot limits requests to the provider only: a slow decode must not hold back the next pages
        // (stuck decodes once held every slot, and the pages after them stayed empty placeholders).
        var holdsSlot = true;
        try
        {
            page.Load = PageLoad.Resolving;
            var bytes = await _provider.LoadAsync(page.Index, page, (w, h) =>
            {
                if (w is > 0 && h is > 0) ApplySize(page.Index, w.Value, h.Value);
                page.Load = PageLoad.Downloading;
            }, token);
            _slots.Release();
            holdsSlot = false;
            token.ThrowIfCancellationRequested();
            PageBytes?.Invoke(page.Index, bytes);
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
        catch (Exception e) when (e is TimeoutException or IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Fail(page, "Network");
        }
        finally
        {
            if (holdsSlot) _slots.Release();
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
        if (ImageFormat.TryGetSize(bytes) is { } natural) page.NaturalWidth = natural.Width;
        var width = DecodeWidth(page.Index);
        var bitmap = new BitmapImage { DecodePixelWidth = width, DecodePixelType = DecodePixelType.Physical };
        // Short of memory the decoder can stall, or decode and then fail to draw (an empty grey slot):
        // both end as a failed page with Retry instead of a placeholder that never fills.
        bitmap.ImageFailed += (_, _) =>
        {
            if (page.Image != bitmap) return;
            page.Image = null;
            page.DecodedWidth = 0;
            Fail(page, "Network");
        };
        using (var stall = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            stall.CancelAfter(DecodeTimeout);
            try
            {
                await bitmap.SetSourceAsync(stream).AsTask(stall.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"page {page.Index + 1} was not decoded in {DecodeTimeout.TotalSeconds} s");
            }
        }
        token.ThrowIfCancellationRequested();
        // The image itself is the final word on proportions (cached pages never saw a PageImage size).
        if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0) ApplySize(page.Index, bitmap.PixelWidth, bitmap.PixelHeight);
        if (page.NaturalWidth == 0 && bitmap.PixelWidth > 0 && bitmap.PixelWidth < width) page.NaturalWidth = bitmap.PixelWidth;
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

    /// <summary>
    /// Physical decode width for the current column, but never wider than the image itself: an
    /// upscaled decode adds no detail, only memory (webtoon pages 690 px wide took ~120 MB each at a
    /// 1500 px column, and a few such pages exhausted the decoder until the released ones were
    /// collected). Narrower still for extremely tall pages and to stay within the largest texture.
    /// </summary>
    private int DecodeWidth(int index)
    {
        var page = Pages[index];
        var width = Geometry.ColumnWidth * Scale;
        if (page.NaturalWidth > 0) width = Math.Min(width, page.NaturalWidth);
        var aspect = Geometry.HeightOf(index) / Geometry.ColumnWidth;
        if (width * width * aspect > MaxDecodedPixels) width = Math.Sqrt(MaxDecodedPixels / aspect);
        if (width * aspect > MaxTextureSize) width = MaxTextureSize / aspect;
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
        _provider.Dispose();
        // A released bitmap's decoded pixels are freed only when its wrapper is collected; reopening
        // a gallery right away must not start next to the previous one's leftovers.
        GC.Collect();
    }
}
