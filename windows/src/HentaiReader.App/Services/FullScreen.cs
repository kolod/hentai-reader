using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace HentaiReader.App.Services;

/// <summary>
/// Borderless full screen (spec FR-016, research R9): the window covers its monitor without title
/// bar and taskbar but stays an ordinary window (no exclusive mode), so Alt+Tab, notifications and
/// display changes behave as usual. Leaving restores the previous size, position or maximized state.
/// </summary>
public sealed class FullScreen(AppWindow window)
{
    private bool _wasMaximized;
    private RectInt32 _bounds;

    public bool IsOn => window.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>Raised after entering or leaving full screen.</summary>
    public event Action<bool>? Changed;

    public void Toggle()
    {
        if (IsOn) Leave();
        else Enter();
    }

    public void Enter()
    {
        if (IsOn) return;
        _wasMaximized = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        _bounds = new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
        window.SetPresenter(AppWindowPresenterKind.FullScreen);
        Changed?.Invoke(true);
    }

    public void Leave()
    {
        if (!IsOn) return;
        window.SetPresenter(AppWindowPresenterKind.Default);
        if (_wasMaximized && window.Presenter is OverlappedPresenter overlapped) overlapped.Maximize();
        else window.MoveAndResize(_bounds);
        Changed?.Invoke(false);
    }
}
