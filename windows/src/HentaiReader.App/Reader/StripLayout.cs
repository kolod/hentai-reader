using HentaiReader.Host.Reading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HentaiReader.App.Reader;

/// <summary>
/// ItemsRepeater layout of the reader strip over <see cref="StripGeometry"/>: elements only for the
/// pages in the realization rect, each exactly the column width at its geometry top, no spacing
/// (FR-004/FR-004a). The extent is the whole strip, so the scroll bar is right before any page loads.
/// </summary>
public sealed class StripLayout : VirtualizingLayout
{
    private StripGeometry? _geometry;

    public StripGeometry? Geometry
    {
        get => _geometry;
        set
        {
            _geometry = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The geometry changed (sizes, column width): measure again.</summary>
    public void Refresh() => InvalidateMeasure();

    private sealed class State
    {
        public int First = -1;
        public int Last = -1;
    }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context) => context.LayoutState = new State();

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context) => context.LayoutState = null;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var state = (State)context.LayoutState;
        if (_geometry is null || _geometry.Count == 0 || context.ItemCount == 0)
        {
            Recycle(context, state, -1, -1);
            state.First = state.Last = -1;
            return new Size(0, 0);
        }

        var rect = context.RealizationRect;
        var (first, last) = rect.Height > 0 ? _geometry.PagesIn(rect.Y, rect.Height) : (0, 0);
        last = Math.Min(last, context.ItemCount - 1);
        Recycle(context, state, first, last);

        var width = _geometry.ColumnWidth;
        for (var i = first; i <= last; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(width, _geometry.HeightOf(i)));
        }
        state.First = first;
        state.Last = last;
        return new Size(width, _geometry.TotalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var state = (State)context.LayoutState;
        if (_geometry is null || state.First < 0) return finalSize;
        var width = _geometry.ColumnWidth;
        // Items may have gone (reader closed) between measure and arrange; never touch indexes beyond them.
        var last = Math.Min(state.Last, Math.Min(context.ItemCount, _geometry.Count) - 1);
        for (var i = state.First; i <= last; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            element.Arrange(new Rect(0, _geometry.TopOf(i), width, _geometry.HeightOf(i)));
        }
        return finalSize;
    }

    /// <summary>Gives back the elements that left [first, last].</summary>
    private static void Recycle(VirtualizingLayoutContext context, State state, int first, int last)
    {
        if (state.First < 0) return;
        for (var i = state.First; i <= state.Last && i < context.ItemCount; i++)
        {
            if (i < first || i > last) context.RecycleElement(context.GetOrCreateElementAt(i));
        }
    }
}
