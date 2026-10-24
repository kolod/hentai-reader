using HentaiReader.Host.Reading;

namespace HentaiReader.Host.Tests.Reading;

public class LoadPlannerTests
{
    private static StripGeometry Strip(double column, int count, double w, double h) =>
        new(Enumerable.Repeat((w, h), count).ToArray(), column);

    [Fact]
    public void Portrait_monitor_with_landscape_pages_loads_all_visible_and_a_full_screen_ahead()
    {
        // 1000 px column, 16:9 pages (562.5 px), viewport 3840 px tall: 7 pages visible.
        var strip = Strip(1000, 100, 1920, 1080);
        var plan = LoadPlanner.Plan(strip, 0, 3840, preloadPages: 4);

        Assert.Equal(Enumerable.Range(0, 7), plan.Visible);
        var aheadHeight = plan.Ahead.Sum(strip.HeightOf);
        Assert.True(plan.Ahead.Count >= 4);
        Assert.True(aheadHeight >= 3840, $"ahead covers {aheadHeight}");
        Assert.Equal(plan.Visible.Last() + 1, plan.Ahead.First());
    }

    [Fact]
    public void Tall_pages_need_only_the_page_count_ahead()
    {
        var strip = Strip(1000, 50, 1000, 3000);                  // each page 3000 px > viewport
        var plan = LoadPlanner.Plan(strip, 0, 1000, preloadPages: 3);
        Assert.Equal([0], plan.Visible);
        Assert.Equal([1, 2, 3], plan.Ahead);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Preload_setting_is_the_minimum_page_count(int n)
    {
        var strip = Strip(1000, 50, 1000, 1500);
        var plan = LoadPlanner.Plan(strip, 0, 1000, n);
        Assert.Equal(n, plan.Ahead.Count);
    }

    [Fact]
    public void Behind_is_at_least_one_page_and_half_a_screen()
    {
        var strip = Strip(1000, 50, 1000, 200);                   // 200 px pages
        var plan = LoadPlanner.Plan(strip, strip.TopOf(20), 1000, 4);
        Assert.Equal([19, 18, 17], plan.Behind);                  // 600 px ≥ 500
    }

    [Fact]
    public void Order_is_visible_then_ahead_then_behind()
    {
        var strip = Strip(1000, 30, 1000, 1500);
        var plan = LoadPlanner.Plan(strip, strip.TopOf(10) + 10, 1000, 3);
        // Page 10 (1500 px) fills the 1000 px view; 3 pages ahead already exceed one screen.
        Assert.Equal([10, 11, 12, 13, 9], plan.Ordered);
        Assert.True(plan.Contains(9));
        Assert.False(plan.Contains(8));
    }

    [Fact]
    public void Edges_of_the_gallery_are_respected()
    {
        var strip = Strip(1000, 5, 1000, 1500);
        var end = LoadPlanner.Plan(strip, strip.TotalHeight - 500, 1000, 4);
        Assert.Empty(end.Ahead);
        Assert.Equal(4, end.Visible.Single());
        var start = LoadPlanner.Plan(strip, 0, 1000, 4);
        Assert.Empty(start.Behind);
    }

    [Fact]
    public void Empty_strip_or_viewport_gives_an_empty_plan()
    {
        Assert.Same(LoadPlan.Empty, LoadPlanner.Plan(Strip(1000, 0, 1, 1), 0, 1000, 4));
        Assert.Same(LoadPlan.Empty, LoadPlanner.Plan(Strip(1000, 3, 1, 1), 0, 0, 4));
    }
}
