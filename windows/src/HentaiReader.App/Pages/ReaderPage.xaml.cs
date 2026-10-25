using System.Text.Json.Nodes;
using HentaiReader.App.Localization;
using HentaiReader.App.Reader;
using HentaiReader.App.Services;
using HentaiReader.Host.Reading;
using HentaiReader.Host.Storage;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace HentaiReader.App.Pages;

/// <summary>
/// The reader, shown in the main window's frame in place of browsing (FR-001): one seamless strip of
/// all pages (US1), back to the gallery (US2), progress (US3), toolbar, scrubber, zoom and full
/// screen (US4). Keys and controls follow specs/005-windows-reader/contracts/reader-ui.md.
/// </summary>
public sealed partial class ReaderPage : Page, IPageInput
{
    public sealed record Args(SourceEntry Source, JsonObject Gallery, int StartPage = 0, double StartOffset = 0);

    private const double MinZoom = 0.5, MaxZoom = 3, ZoomStep = 0.1;
    private static readonly TimeSpan ToolbarDelay = TimeSpan.FromSeconds(2);

    private ReaderSession? _session;
    private Args? _args;
    private double _zoom = 1;
    private bool _fitWidth;
    private bool _gaps;
    private bool _positioned;
    private bool _reachedEnd;
    private bool _syncingScrubber;
    private bool _scrubbing;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = ToolbarDelay };

    public ReaderPage()
    {
        InitializeComponent();
        PageSlot.RetryRequested += OnRetryRequested;
        Unloaded += (_, _) => PageSlot.RetryRequested -= OnRetryRequested;
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveProgress();
        };
        _hideTimer.Tick += (_, _) =>
        {
            // Never hide the scrubber while it is being dragged.
            if (!_scrubbing) ShowChrome(false);
        };

        Label(BackButton, "Gallery_Back");
        Label(GoToButton, "Reader_GoToPage");
        Label(ZoomOutButton, "Reader_ZoomOut");
        Label(ZoomInButton, "Reader_ZoomIn");
        Label(FitWidthButton, "Reader_FitWidth");
        Label(GapButton, "Reader_PageGaps");
        Label(FullScreenButton, "Reader_FullScreen");
        AutomationProperties.SetName(Scrubber, Strings.Get("Reader_Page"));
        LimitRetryButton.Content = Strings.Get("Search_Retry");

        // Dragging the scrubber previews the target; the jump happens on release.
        // The scroll viewer handles pointer moves; the toolbar still wants to see them.
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), handledEventsToo: true);
        Scrubber.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), handledEventsToo: true);
        Scrubber.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);
        Scrubber.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);
        // The scrubber lies over the strip, not inside it: wheel turns over it would stop there.
        Scrubber.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Scrubber_PointerWheelChanged), handledEventsToo: true);
    }

    private static void Label(ButtonBase button, string key)
    {
        var text = Strings.Get(key);
        ToolTipService.SetToolTip(button, text);
        AutomationProperties.SetName(button, text);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Args args) return;
        _args = args;
        var preferences = AppServices.Preferences;
        _session = new ReaderSession(args.Source, args.Gallery, AppServices.PageCache, preferences.ReadingWidth)
        {
            PreloadPages = Math.Clamp(preferences.PreloadPages, 3, 5),
        };
        _session.PageSizeChanged += OnPageSizeChanged;
        _session.Limited += OnLimited;
        Layout.Geometry = _session.Geometry;
        Strip.ItemsSource = _session.Pages;
        _positioned = false;
        _reachedEnd = false;
        _zoom = 1;
        _fitWidth = preferences.FitWidth;
        // Webtoons are drawn as one continuous strip: they start without gaps.
        _gaps = preferences.PageGap > 0 && !IsWebtoon(args.Gallery);
        _session.Geometry.SetGap(_gaps ? preferences.PageGap : 0);
        GapButton.IsChecked = _gaps;
        GapButton.IsEnabled = preferences.PageGap > 0;
        TitleText.Text = _session.Title;
        Scrubber.Maximum = Math.Max(1, _session.Pages.Count);
        Scrubber.Visibility = _session.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateZoomText();
        LimitBar.IsOpen = false;
        if (MainWindow.Instance is { } window)
        {
            window.Closed += Window_Closed;
            window.FullScreen.Changed += OnFullScreenChanged;
            OnFullScreenChanged(window.FullScreen.IsOn);
            // The reader always takes the whole window; navigation comes back with the gallery.
            window.NavigationPaneVisible = false;
        }
        ShowChrome(true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopGlide();
        _saveTimer.Stop();
        _hideTimer.Stop();
        SaveProgress();
        if (MainWindow.Instance is { } window)
        {
            window.Closed -= Window_Closed;
            window.FullScreen.Changed -= OnFullScreenChanged;
            window.NavigationPaneVisible = true;
        }
        _session?.Dispose();
        _session = null;
        Strip.ItemsSource = null;
    }

    /// <summary>Escape leaves full screen first, then goes back to the gallery (FR-003).</summary>
    public bool OnEscape()
    {
        if (MainWindow.Instance is { FullScreen.IsOn: true } window)
        {
            window.FullScreen.Leave();
            return true;
        }
        MainWindow.Instance?.GoBack();
        return true;
    }

    /// <summary>Keys scroll the strip, so it gets the focus (not the first toolbar button).</summary>
    public Control InitialFocus => Scroller;

    private double ViewportWidth => Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth;

    private double ColumnWidth => StripGeometry.ColumnWidthFor(ViewportWidth, AppServices.Preferences.ReadingWidth, _zoom, _fitWidth);

    private static bool IsWebtoon(JsonObject gallery) =>
        (gallery["tags"] as JsonArray ?? []).Any(t => string.Equals(t?["name"]?.GetValue<string>(), "webtoon", StringComparison.OrdinalIgnoreCase));

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_session is null || Scroller.ViewportWidth <= 0) return;
        _session.Scale = XamlRoot?.RasterizationScale ?? 1;
        var position = _positioned ? _session.Geometry.PositionAt(Scroller.VerticalOffset) : (_args!.StartPage, _args.StartOffset);
        Relayout(position, 0);
        _positioned = true;
        if (FocusState == FocusState.Unfocused) Scroller.Focus(FocusState.Programmatic);
    }

    /// <summary>Applies the current column width and puts <paramref name="position"/> at <paramref name="screenY"/>.</summary>
    private void Relayout((int Page, double Offset) position, double screenY)
    {
        StopGlide();
        _session!.SetColumnWidth(ColumnWidth);
        Layout.Refresh();
        Strip.UpdateLayout();
        ScrollTo(_session.Geometry.ScrollFor(position.Page, position.Offset) - screenY);
        // ChangeView to an unchanged offset raises no ViewChanged, so plan explicitly.
        Plan();
        UpdatePageIndicator();
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate) _wheelTarget = double.NaN;
        Plan();
        if (_session is null || !_positioned) return;
        if (Scroller.VerticalOffset + Scroller.ViewportHeight >= _session.Geometry.TotalHeight - 1) _reachedEnd = true;
        UpdatePageIndicator();
        // Saved 5 s after scrolling stops (FR-018).
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Window_Closed(object sender, WindowEventArgs args) => SaveProgress();

    /// <summary>Page and fraction of it at the top of the view; read once the last page was fully in view.</summary>
    private void SaveProgress()
    {
        if (_session is null || !_positioned || _session.Pages.Count == 0) return;
        var (page, offset) = _session.Geometry.PositionAt(Scroller.VerticalOffset);
        AppServices.Progress.Set(_session.SourceId, _session.GalleryId,
            new ReadingProgress(page, offset, _session.Pages.Count, _reachedEnd, DateTimeOffset.UtcNow, _session.Title));
    }

    private void Plan() => _session?.UpdateView(Scroller.VerticalOffset, Scroller.ViewportHeight);

    /// <summary>
    /// An exact size arrived. A page entirely above the view shifts the scroll offset by exactly
    /// its height change, so the content in view stays where it is (FR-006, research R3).
    /// </summary>
    private void OnPageSizeChanged(int page, double delta)
    {
        if (_session is null) return;
        // While gliding, the glide position is ahead of the reported offset.
        var offset = _gliding ? _glideY : Scroller.VerticalOffset;
        var wasAbove = _session.Geometry.TopOf(page) + _session.Geometry.HeightOf(page) - delta <= offset + 0.5;
        Layout.Refresh();
        Strip.UpdateLayout();
        if (!wasAbove || delta == 0) return;
        if (_gliding) _glideY += delta;
        ScrollTo(offset + delta);
    }

    // --- scroll keys: continuous scroll with inertia ----------------------------------------------

    /// <param name="Speed">
    /// Top speed in column widths per second: tied to the size of the pages, not of the window, so
    /// pages pass at the same rate on a portrait or landscape monitor and at any zoom. Calibrated on
    /// a portrait 1080×1920 window at fit width (1.2 and 4 window heights per second there).
    /// </param>
    /// <param name="EaseIn">How fast the speed is reached (1/s).</param>
    /// <param name="EaseOut">How fast it settles after release (1/s).</param>
    private sealed record GlideProfile(double Speed, double EaseIn, double EaseOut);

    private static readonly GlideProfile ArrowGlide = new(2.1, 10, 6);
    private static readonly GlideProfile PageGlide = new(7.1, 18, 10);

    private GlideProfile _glideProfile = ArrowGlide;
    private VirtualKey _glideKey;
    private int _glideDirection;
    private double _glideVelocity;
    private double _glideY;
    private TimeSpan _glideLast;
    private bool _gliding;

    /// <summary>
    /// Key repeats come only ~30 times a second and each restarted a short scroll animation, so a
    /// held key moved the strip in jerks. Instead the strip moves every frame while the key is
    /// down: the speed eases in, and after release it eases out (inertia). Page keys and Space are
    /// the same, only faster: no fixed page-sized steps, so a short press never springs back.
    /// </summary>
    private void StartGlide(VirtualKey key, int direction, GlideProfile profile)
    {
        if (!_gliding)
        {
            _gliding = true;
            _glideY = Scroller.VerticalOffset;
            _glideVelocity = 0;
            _glideLast = TimeSpan.Zero;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += GlideStep;
        }
        _glideKey = key;
        _glideDirection = direction;
        _glideProfile = profile;
    }

    private void StopGlide()
    {
        if (!_gliding) return;
        _gliding = false;
        _glideVelocity = 0;
        _glideDirection = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= GlideStep;
    }

    private void GlideStep(object? sender, object e)
    {
        var now = ((Microsoft.UI.Xaml.Media.RenderingEventArgs)e).RenderingTime;
        var dt = _glideLast == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _glideLast).TotalSeconds, 0, 0.1);
        _glideLast = now;
        if (dt == 0) return;
        var p = _glideProfile;
        var top = p.Speed * (_session?.Geometry.ColumnWidth ?? Scroller.ViewportWidth);
        // Key released (also when the window lost the key-up).
        if (_glideDirection != 0 && !InputKeyboardSource.GetKeyStateForCurrentThread(_glideKey).HasFlag(CoreVirtualKeyStates.Down)) _glideDirection = 0;

        var target = _glideDirection * top;
        var rate = _glideDirection != 0 ? p.EaseIn : p.EaseOut;
        _glideVelocity += (target - _glideVelocity) * Math.Min(1, dt * rate);

        var max = Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight);
        _glideY = Math.Clamp(_glideY + _glideVelocity * dt, 0, max);
        Scroller.ChangeView(null, _glideY, null, disableAnimation: true);
        var settled = _glideDirection == 0 && Math.Abs(_glideVelocity) < 20;
        var atEdge = _glideY <= 0 && _glideVelocity < 0 || _glideY >= max && _glideVelocity > 0;
        if (settled || atEdge) StopGlide();
    }

    private void ScrollTo(double y, bool animate = false) =>
        Scroller.ChangeView(null, Math.Max(0, y), null, disableAnimation: !animate);

    private void JumpToPage(int page)
    {
        if (_session is null) return;
        StopGlide();
        ScrollTo(_session.Geometry.TopOf(Math.Clamp(page, 0, _session.Pages.Count - 1)));
    }

    private void OnRetryRequested(PageState page)
    {
        if (_session?.Pages.Contains(page) == true) _session.Retry(page);
    }

    // --- page indicator and scrubber ------------------------------------------------------------

    private void UpdatePageIndicator()
    {
        if (_session is null || _session.Pages.Count == 0) return;
        var page = _session.Geometry.PageAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 3) + 1;
        PageText.Text = Strings.Format("Reader_PageOf", page, _session.Pages.Count);
        if (_scrubbing) return;
        _syncingScrubber = true;
        Scrubber.Value = page;
        _syncingScrubber = false;
    }

    private void Scrubber_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScrubber || _scrubbing) return;
        // Keyboard on the scrubber: jump at once.
        JumpToPage((int)e.NewValue - 1);
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        JumpToPage((int)Scrubber.Value - 1);
    }

    private async void GoToButton_Click(object sender, RoutedEventArgs e) => await GoToPageAsync();

    private async Task GoToPageAsync()
    {
        if (_session is null) return;
        var total = _session.Pages.Count;
        // A plain text box: a NumberBox clamps or reverts out-of-range input and commits its value
        // only after the dialog's Enter, so the message of FR-014 could never appear.
        var box = new TextBox
        {
            Header = Strings.Format("Reader_GoToPage_Prompt", total),
            Text = ((int)Scrubber.Value).ToString(Strings.Culture),
            InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
        };
        box.Loaded += (_, _) => box.SelectAll();
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("Reader_GoToPage"),
            Content = new StackPanel { Spacing = 8, Children = { box, error } },
            PrimaryButtonText = Strings.Get("Reader_Go"),
            CloseButtonText = Strings.Get("Reader_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var target = 0;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (int.TryParse(box.Text.Trim(), System.Globalization.NumberStyles.None, Strings.Culture, out var page) && page >= 1 && page <= total)
            {
                target = page;
                return;
            }
            error.Text = Strings.Format("Reader_GoToPage_Invalid", total);
            error.Visibility = Visibility.Visible;
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) JumpToPage(target - 1);
        Scroller.Focus(FocusState.Programmatic);
    }

    // --- zoom -------------------------------------------------------------------------------------

    /// <summary>Changes the column width, keeping the strip point at <paramref name="screenY"/> in place (FR-015).</summary>
    private void SetZoom(double zoom, double screenY)
    {
        if (_session is null) return;
        zoom = Math.Clamp(Math.Round(zoom, 2), MinZoom, MaxZoom);
        if (zoom == _zoom) return;
        var anchor = _session.Geometry.PositionAt(Scroller.VerticalOffset + screenY);
        _zoom = zoom;
        Relayout(anchor, screenY);
        UpdateZoomText();
    }

    /// <summary>
    /// Back to 100 % of the window width (<paramref name="fit"/>) or of the reading width. Zoom is
    /// relative to that base, so any zoom step leaves "fit width" until W or Ctrl+0.
    /// </summary>
    private void SetWidthMode(bool fit)
    {
        if (_session is null) return;
        if (fit != _fitWidth || _zoom != 1)
        {
            var anchor = _session.Geometry.PositionAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 2);
            _fitWidth = fit;
            _zoom = 1;
            Relayout(anchor, Scroller.ViewportHeight / 2);
        }
        UpdateZoomText();
    }

    /// <summary>Gaps between pages on or off, keeping the middle of the view in place.</summary>
    private void SetGaps(bool on)
    {
        var gap = AppServices.Preferences.PageGap;
        on &= gap > 0;
        GapButton.IsChecked = on;
        if (_session is null || on == _gaps) return;
        var anchor = _session.Geometry.PositionAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 2);
        _gaps = on;
        _session.Geometry.SetGap(on ? gap : 0);
        Relayout(anchor, Scroller.ViewportHeight / 2);
    }

    private void FitWidthButton_Click(object sender, RoutedEventArgs e) => SetWidthMode(fit: true);

    private void GapButton_Click(object sender, RoutedEventArgs e) => SetGaps(GapButton.IsChecked == true);

    /// <summary>Zoom value; the fit-width button is lit only while the page is exactly the window width.</summary>
    private void UpdateZoomText()
    {
        ZoomText.Text = Strings.Format("Reader_ZoomValue", (int)Math.Round(_zoom * 100));
        FitWidthButton.IsChecked = _fitWidth && _zoom == 1;
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + ZoomStep, Scroller.ViewportHeight / 2);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - ZoomStep, Scroller.ViewportHeight / 2);

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        var point = e.GetCurrentPoint(Scroller);
        SetZoom(_zoom + Math.Sign(point.Properties.MouseWheelDelta) * ZoomStep, point.Position.Y);
        e.Handled = true;
    }

    /// <summary>Approximate step of one wheel notch in the strip, as a share of its height (measured).</summary>
    private const double WheelStep = 0.15;
    private double _wheelTarget = double.NaN;

    /// <summary>
    /// Wheel over the scrubber scrolls the strip (Ctrl+wheel zooms), as anywhere else; the
    /// scrubber itself only reacts to clicks and drags.
    /// </summary>
    private void Scrubber_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_session is null) return;
        var point = e.GetCurrentPoint(Scroller);
        var notches = point.Properties.MouseWheelDelta / 120.0;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            SetZoom(_zoom + Math.Sign(notches) * ZoomStep, point.Position.Y);
            return;
        }
        StopGlide();
        // Successive notches add up even while the previous animation is still running.
        var from = double.IsNaN(_wheelTarget) ? Scroller.VerticalOffset : _wheelTarget;
        var max = Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight);
        _wheelTarget = Math.Clamp(from - notches * WheelStep * Scroller.ViewportHeight, 0, max);
        ScrollTo(_wheelTarget, animate: true);
    }

    // --- full screen and toolbar ------------------------------------------------------------------

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.FullScreen.Toggle();

    private void OnFullScreenChanged(bool on)
    {
        FullScreenIcon.Symbol = on ? Symbol.BackToWindow : Symbol.FullScreen;
        Label(FullScreenButton, on ? "Reader_ExitFullScreen" : "Reader_FullScreen");
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.GoBack();

    /// <summary>
    /// The source signalled a limit: nothing more is requested until Retry (FR-021, Constitution VI).
    /// Sources with account status also show where the image limit stands.
    /// </summary>
    private void OnLimited(double? retryAfterSeconds)
    {
        if (_session is null || LimitBar.IsOpen) return;
        var message = Strings.ErrorBody("RateLimited");
        if (retryAfterSeconds is { } seconds) message += " " + Strings.Format("Error_RateLimited_After", Math.Max(1, (int)Math.Ceiling(seconds / 60)));
        LimitBar.Title = Strings.ErrorTitle("RateLimited");
        LimitBar.Message = message;
        LimitBar.IsOpen = true;
        ShowChrome(true);

        // No request may go out while limited, so the last known account figures are shown (FR-027).
        if (_args?.Source.LastAccountStatus?["imageLimit"] is JsonObject limit)
            LimitBar.Message = message + " " + Strings.Format("Account_ImageLimit", limit["used"]!.GetValue<double>(), limit["max"]!.GetValue<double>());
    }

    private void LimitRetryButton_Click(object sender, RoutedEventArgs e)
    {
        LimitBar.IsOpen = false;
        _session?.RetryAfterLimit();
        Plan();
    }

    /// <summary>The toolbar and scrubber return when the pointer is near the top or right edge (FR-017).</summary>
    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Root).Position;
        if (p.Y < 80 || p.X > Root.ActualWidth - 80) ShowChrome(true);
    }

    private void ShowChrome(bool show)
    {
        _hideTimer.Stop();
        Toolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_session?.Pages.Count > 1) Scrubber.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) _hideTimer.Start();
    }

    /// <summary>Space / PgDn, Shift+Space / PgUp, Home, End, Up, Down, Ctrl+G, Ctrl+Plus/Minus/0 (contracts/reader-ui.md).</summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is null) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl)
        {
            var handled = true;
            switch (e.Key)
            {
                case VirtualKey.G:
                    ShowChrome(true);
                    _ = GoToPageAsync();
                    break;
                case VirtualKey.Add or (VirtualKey)187:   // numpad + or "=+" key
                    SetZoom(_zoom + ZoomStep, Scroller.ViewportHeight / 2);
                    break;
                case VirtualKey.Subtract or (VirtualKey)189:   // numpad − or "-_" key
                    SetZoom(_zoom - ZoomStep, Scroller.ViewportHeight / 2);
                    break;
                case VirtualKey.Number0 or VirtualKey.NumberPad0:
                    SetWidthMode(AppServices.Preferences.FitWidth);
                    break;
                default:
                    handled = false;
                    break;
            }
            e.Handled = handled;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.W:
                SetWidthMode(fit: true);
                e.Handled = true;
                return;
            case VirtualKey.G:
                SetGaps(!_gaps);
                e.Handled = true;
                return;
        }
        int? direction = e.Key switch
        {
            VirtualKey.Down or VirtualKey.PageDown => 1,
            VirtualKey.Up or VirtualKey.PageUp => -1,
            VirtualKey.Space => shift ? -1 : 1,
            _ => null,
        };
        if (direction is { } d)
        {
            // Held keys glide (key repeats are ignored); see GlideStep.
            if (!e.KeyStatus.WasKeyDown) StartGlide(e.Key, d, e.Key is VirtualKey.Up or VirtualKey.Down ? ArrowGlide : PageGlide);
            e.Handled = true;
            return;
        }
        if (e.Key is VirtualKey.Home or VirtualKey.End)
        {
            StopGlide();
            ScrollTo(e.Key == VirtualKey.Home ? 0 : _session.Geometry.TotalHeight);
            e.Handled = true;
        }
    }
}
