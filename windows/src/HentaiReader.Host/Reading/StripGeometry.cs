namespace HentaiReader.Host.Reading;

/// <summary>
/// Layout of the reader strip (specs/005-windows-reader/data-model.md "Strip geometry"): every page
/// is exactly the column width, whatever its resolution (FR-004a); its height follows its own
/// aspect ratio; pages touch. Units are device-independent pixels.
/// </summary>
public sealed class StripGeometry
{
    /// <summary>Aspect used when a source gives no usable size: portrait 2:3.</summary>
    public const double DefaultWidth = 2, DefaultHeight = 3;

    private readonly double[] _width;
    private readonly double[] _height;
    private readonly double[] _tops;

    public StripGeometry(IReadOnlyList<(double Width, double Height)> sizes, double columnWidth)
    {
        if (columnWidth <= 0) throw new ArgumentOutOfRangeException(nameof(columnWidth));
        _width = new double[sizes.Count];
        _height = new double[sizes.Count];
        _tops = new double[sizes.Count + 1];
        for (var i = 0; i < sizes.Count; i++) (_width[i], _height[i]) = Normalize(sizes[i].Width, sizes[i].Height);
        ColumnWidth = columnWidth;
        Recompute(0);
    }

    public int Count => _width.Length;
    public double ColumnWidth { get; private set; }
    public double TotalHeight => _tops[^1];

    /// <summary>Column width for a viewport: the reading width, narrower windows use their width, times zoom.</summary>
    public static double ColumnWidthFor(double viewportWidth, double readingWidth, double zoom) =>
        Math.Max(1, Math.Min(viewportWidth, readingWidth)) * zoom;

    public double TopOf(int page) => _tops[page];
    public double HeightOf(int page) => _tops[page + 1] - _tops[page];

    public void SetColumnWidth(double columnWidth)
    {
        if (columnWidth <= 0) throw new ArgumentOutOfRangeException(nameof(columnWidth));
        if (columnWidth == ColumnWidth) return;
        ColumnWidth = columnWidth;
        Recompute(0);
    }

    /// <summary>
    /// Replaces a page's (estimated) size with its exact size. Returns the change of that page's
    /// height; the caller keeps the view still when the page lies above the viewport (FR-006).
    /// </summary>
    public double SetPageSize(int page, double width, double height)
    {
        var before = HeightOf(page);
        (_width[page], _height[page]) = Normalize(width, height);
        Recompute(page);
        return HeightOf(page) - before;
    }

    /// <summary>Page whose span contains <paramref name="y"/> (clamped to the strip).</summary>
    public int PageAt(double y)
    {
        if (Count == 0) return -1;
        if (y <= 0) return 0;
        if (y >= TotalHeight) return Count - 1;
        int lo = 0, hi = Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_tops[mid] <= y) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Page at <paramref name="y"/> and the fraction (0–1) of its height above y.</summary>
    public (int Page, double Offset) PositionAt(double y)
    {
        var page = PageAt(y);
        if (page < 0) return (0, 0);
        var h = HeightOf(page);
        return (page, h > 0 ? Math.Clamp((y - _tops[page]) / h, 0, 1) : 0);
    }

    /// <summary>Scroll offset that puts the given position at the top of the view.</summary>
    public double ScrollFor(int page, double offset)
    {
        if (Count == 0) return 0;
        page = Math.Clamp(page, 0, Count - 1);
        return _tops[page] + Math.Clamp(offset, 0, 1) * HeightOf(page);
    }

    /// <summary>First and last page intersecting [y, y + height); (-1, -1) for an empty strip.</summary>
    public (int First, int Last) PagesIn(double y, double height)
    {
        if (Count == 0) return (-1, -1);
        var first = PageAt(y);
        var last = PageAt(Math.Max(y, y + height - 0.001));
        return (first, last);
    }

    private static (double, double) Normalize(double width, double height) =>
        width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height) ? (width, height) : (DefaultWidth, DefaultHeight);

    private void Recompute(int from)
    {
        for (var i = from; i < Count; i++) _tops[i + 1] = _tops[i] + ColumnWidth * _height[i] / _width[i];
    }
}
