namespace HentaiReader.Host.Reading;

/// <summary>Pages to load and keep decoded, in priority order.</summary>
public sealed record LoadPlan(IReadOnlyList<int> Visible, IReadOnlyList<int> Ahead, IReadOnlyList<int> Behind)
{
    public static readonly LoadPlan Empty = new([], [], []);

    /// <summary>Visible (top → bottom), then ahead (near → far), then behind (near → far).</summary>
    public IEnumerable<int> Ordered => Visible.Concat(Ahead).Concat(Behind);

    public bool Contains(int page) => Visible.Contains(page) || Ahead.Contains(page) || Behind.Contains(page);
}

/// <summary>
/// The load and memory window (data-model.md "Load window", FR-008/FR-008a): everything visible,
/// whatever the count; ahead at least N pages and one screen; behind at least one page and half a
/// screen. Sized by screen height so many small pages in view (landscape pages on a portrait
/// monitor) still leave a full screen loaded ahead.
/// </summary>
public static class LoadPlanner
{
    public static LoadPlan Plan(StripGeometry strip, double scrollY, double viewportHeight, int preloadPages)
    {
        if (strip.Count == 0 || viewportHeight <= 0) return LoadPlan.Empty;
        var (first, last) = strip.PagesIn(scrollY, viewportHeight);

        var visible = new List<int>();
        for (var i = first; i <= last; i++) visible.Add(i);

        var ahead = new List<int>();
        var covered = 0.0;
        for (var i = last + 1; i < strip.Count && (ahead.Count < preloadPages || covered < viewportHeight); i++)
        {
            ahead.Add(i);
            covered += strip.HeightOf(i);
        }

        var behind = new List<int>();
        covered = 0;
        for (var i = first - 1; i >= 0 && (behind.Count < 1 || covered < viewportHeight / 2); i--)
        {
            behind.Add(i);
            covered += strip.HeightOf(i);
        }

        return new LoadPlan(visible, ahead, behind);
    }
}
