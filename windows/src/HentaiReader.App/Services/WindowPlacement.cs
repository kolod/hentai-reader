using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace HentaiReader.App.Services;

/// <summary>Window size and position kept between sessions (physical pixels).</summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>
/// Restores the main window where it was closed and remembers its last normal bounds. A position
/// that is no longer on any display (monitor unplugged) is ignored and the system default is kept.
/// Full screen is not restored; the window comes back as it was before entering it.
/// </summary>
public sealed class WindowPlacement
{
    private const int MinSize = 320;

    private readonly AppWindow _window;
    private RectInt32? _normal;
    private bool _maximized;

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
                if (saved.Maximized && window.Presenter is OverlappedPresenter overlapped) overlapped.Maximize();
            }
        }
        Track();
        window.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) Track();
        };
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
                _maximized = false;
                _normal = new RectInt32(_window.Position.X, _window.Position.Y, _window.Size.Width, _window.Size.Height);
                break;
        }
    }
}
