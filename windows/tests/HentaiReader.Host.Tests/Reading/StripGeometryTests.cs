using HentaiReader.Host.Reading;

namespace HentaiReader.Host.Tests.Reading;

public class StripGeometryTests
{
    private static StripGeometry Strip(double column, params (double, double)[] sizes) => new(sizes, column);

    [Fact]
    public void Every_page_is_column_wide_whatever_its_resolution()
    {
        // Low-resolution cover, normal page, high-resolution page, two-page spread, notes page.
        var strip = Strip(1000, (800, 1131), (1280, 1810), (2560, 3620), (2560, 1810), (1000, 400));
        Assert.Equal(1000 * 1131 / 800.0, strip.HeightOf(0), 6);
        Assert.Equal(1000 * 1810 / 1280.0, strip.HeightOf(1), 6);
        Assert.Equal(strip.HeightOf(1), strip.HeightOf(2), 6);              // same proportions, any resolution
        Assert.Equal(1000 * 1810 / 2560.0, strip.HeightOf(3), 6);           // spread: half the height
        Assert.Equal(400, strip.HeightOf(4), 6);
    }

    [Fact]
    public void Pages_touch_and_total_is_the_sum()
    {
        var strip = Strip(500, (2, 3), (1, 1), (4, 1));
        Assert.Equal(0, strip.TopOf(0));
        Assert.Equal(750, strip.TopOf(1));
        Assert.Equal(1250, strip.TopOf(2));
        Assert.Equal(1375, strip.TotalHeight);
    }

    [Fact]
    public void Unknown_sizes_default_to_portrait_two_by_three()
    {
        var strip = Strip(300, (0, 0), (double.NaN, 5), (-1, 2));
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(450, strip.HeightOf(i), 6));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(749.9, 0)]
    [InlineData(750, 1)]
    [InlineData(1374, 2)]
    [InlineData(-10, 0)]
    [InlineData(99999, 2)]
    public void PageAt_finds_the_page_containing_y(double y, int expected)
    {
        Assert.Equal(expected, Strip(500, (2, 3), (1, 1), (4, 1)).PageAt(y));
    }

    [Fact]
    public void Position_survives_width_and_zoom_changes()
    {
        var strip = Strip(1000, (2, 3), (3, 4), (16, 9), (2, 3));
        var y = strip.TopOf(2) + strip.HeightOf(2) * 0.25;
        var (page, offset) = strip.PositionAt(y);
        Assert.Equal((2, 0.25), (page, Math.Round(offset, 9)));

        strip.SetColumnWidth(StripGeometry.ColumnWidthFor(1400, 1000, 1.5));
        var moved = strip.ScrollFor(page, offset);
        Assert.Equal((2, 0.25), (strip.PositionAt(moved).Page, Math.Round(strip.PositionAt(moved).Offset, 9)));
    }

    [Fact]
    public void Exact_size_reports_the_height_delta_and_shifts_only_later_pages()
    {
        var strip = Strip(1000, (2, 3), (2, 3), (2, 3));
        var topOfLast = strip.TopOf(2);
        var delta = strip.SetPageSize(1, 1000, 2000);          // estimate 1500 → exact 2000
        Assert.Equal(500, delta, 6);
        Assert.Equal(0, strip.TopOf(0));
        Assert.Equal(1500, strip.TopOf(1));                    // the page itself does not move
        Assert.Equal(topOfLast + 500, strip.TopOf(2));
    }

    [Fact]
    public void Correction_above_the_view_keeps_the_view_still_when_the_delta_is_applied()
    {
        var strip = Strip(1000, (2, 3), (2, 3), (2, 3), (2, 3));
        var scroll = strip.TopOf(2) + 100;                     // reading page 2
        var before = strip.PositionAt(scroll);
        var delta = strip.SetPageSize(0, 2, 4);                 // page 0 above the view grows
        var after = strip.PositionAt(scroll + delta);
        Assert.Equal(before.Page, after.Page);
        Assert.Equal(before.Offset, after.Offset, 9);
    }

    [Theory]
    [InlineData(1920, 1000, 1, 1000)]
    [InlineData(700, 1000, 1, 700)]
    [InlineData(1920, 1000, 0.5, 500)]
    [InlineData(1920, 1000, 2, 2000)]
    public void Column_width_is_reading_width_or_narrower_window_times_zoom(double viewport, double reading, double zoom, double expected)
    {
        Assert.Equal(expected, StripGeometry.ColumnWidthFor(viewport, reading, zoom));
    }

    [Fact]
    public void PagesIn_spans_the_viewport()
    {
        var strip = Strip(100, Enumerable.Repeat((1.0, 1.0), 10).ToArray());   // 100 px each
        Assert.Equal((2, 4), strip.PagesIn(250, 200));
        Assert.Equal((3, 3), strip.PagesIn(300, 100));
    }
}
