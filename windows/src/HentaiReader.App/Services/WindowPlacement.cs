using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace HentaiReader.App.Services;

/// <summary>Window size and position kept between sessions (physical pixels); full screen on top of them.</summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized, bool FullScreen = false);

/// <summary>
/// Restores the main window where it was closed and remembers its last normal bounds. A position
/// that is no longer on any display (monitor unplugged) is ignored and the system default is kept.
/// Full screen is restored by the main window on top of these bounds, so leaving it returns to them.
/// </summary>
public sealed class WindowPlacement
{
    private const int MinSize = 320;

    private readonly AppWindow _window;
    private RectInt32? _normal;
    private bool _maximized;
    private bool _maximizeOnShow;

    public WindowPlacement(AppWindow window, WindowBounds? saved)
    {
        _window = window;
        if (saved is { Width: >= MinSize, Height: >= MinSize })
        {
            var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null)
            {
                window.MoveAndResize(rect);
                _normal = rect;
                _maximizeOnShow = saved.Maximized;
            }
        }
        Track();
        window.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) Track();
        };
    }

    /// <summary>
    /// After the window was first shown: a maximize before that was undone by the show (the window came
    /// back restored, and was then saved as not maximized).
    /// </summary>
    public void ApplyAfterShow()
    {
        if (!_maximizeOnShow) return;
        _maximizeOnShow = false;
        if (_window.Presenter is OverlappedPresenter overlapped) overlapped.Maximize();
    }

    /// <summary>Bounds to save: the last normal (not maximized, not full screen) rectangle.</summary>
    public WindowBounds? Current => _normal is { } r ? new WindowBounds(r.X, r.Y, r.Width, r.Height, _maximized) : null;

    private void Track()
    {
        if (_window.Presenter is not OverlappedPresenter overlapped) return;   // full screen: keep what was before
        switch (overlapped.State)
        {
            case OverlappedPresenterState.Maximized:
                _maximized = true;
                break;
            case OverlappedPresenterState.Restored:
                var rect = new RectInt32(_window.Position.X, _window.Position.Y, _window.Size.Width, _window.Size.Height);
                // Leaving full screen passes through a restored window the size of the monitor before it
                // is maximized again; that is no size the user chose.
                if (CoversDisplay(rect)) break;
                _maximized = false;
                _normal = rect;
                break;
        }
    }

    private static bool CoversDisplay(RectInt32 rect) =>
        DisplayArea.GetFromRect(rect, DisplayAreaFallback.None)?.OuterBounds is { } d
        && rect.X <= d.X && rect.Y <= d.Y && rect.X + rect.Width >= d.X + d.Width && rect.Y + rect.Height >= d.Y + d.Height;
}
