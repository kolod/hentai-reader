using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace HentaiReader.App.Services;

/// <summary>
/// Borderless full screen (spec FR-016, research R9): the window covers its monitor without title
/// bar and taskbar but stays an ordinary window (no exclusive mode), so Alt+Tab, notifications and
/// display changes behave as usual. Leaving restores the previous size, position or maximized state.
/// The switch is instant: the system's resize animation made the strip re-layout on every frame.
/// </summary>
public sealed partial class FullScreen(AppWindow window)
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
        WithoutAnimation(() => window.SetPresenter(AppWindowPresenterKind.FullScreen));
        Changed?.Invoke(true);
    }

    public void Leave()
    {
        if (!IsOn) return;
        WithoutAnimation(() =>
        {
            window.SetPresenter(AppWindowPresenterKind.Default);
            if (_wasMaximized && window.Presenter is OverlappedPresenter overlapped) overlapped.Maximize();
            else window.MoveAndResize(_bounds);
        });
        Changed?.Invoke(false);
    }

    /// <summary>Runs a presenter change with DWM transitions off; they come back for minimize and restore.</summary>
    private void WithoutAnimation(Action change)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id);
        SetTransitions(hwnd, disabled: true);
        try
        {
            change();
        }
        finally
        {
            // The animation is started asynchronously by DWM; re-enable once it would have begun.
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(500);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => SetTransitions(hwnd, disabled: false);
            timer.Start();
        }
    }

    private const int DwmwaTransitionsForceDisabled = 3;

    private static void SetTransitions(IntPtr hwnd, bool disabled)
    {
        var value = disabled ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref value, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
