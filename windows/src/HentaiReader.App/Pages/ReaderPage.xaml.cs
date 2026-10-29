using System.Text.Json.Nodes;
using HentaiReader.App.Localization;
using HentaiReader.App.Reader;
using HentaiReader.App.Services;
using HentaiReader.Host.Data;
using HentaiReader.Host.Reading;
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
    /// <summary>What to read: a gallery online through its source, or a saved copy (006).</summary>
    public sealed record Args(SourceEntry? Source, JsonObject? Gallery, LibraryItem? Saved, int StartPage = 0, double StartOffset = 0)
    {
        public static Args Online(SourceEntry source, JsonObject gallery, int page = 0, double offset = 0) => new(source, gallery, null, page, offset);
        public static Args Offline(LibraryItem saved, int page = 0, double offset = 0) => new(null, null, saved, page, offset);
    }

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
        Loaded += (_, _) => SaveTracker.Changed += OnSaveChanged;
        Unloaded += (_, _) => SaveTracker.Changed -= OnSaveChanged;
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
        Strip.PointerWheelChanged += Strip_PointerWheelChanged;
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

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Args args) return;
        _args = args;
        if (MainWindow.Instance is { } window)
        {
            window.FullScreen.Changed += OnFullScreenChanged;
            OnFullScreenChanged(window.FullScreen.IsOn);
            // The reader always takes the whole window; navigation comes back with the gallery.
            window.NavigationPaneVisible = false;
        }
        var preferences = AppServices.Preferences;
        IPageProvider provider;
        try
        {
            provider = args.Saved is { } saved
                ? await Task.Run(() => new ArchivePageProvider(saved.FullPath, saved.Archive.Key, saved.Archive.Title, saved.Archive.Pages))
                : new OnlinePageProvider(args.Source!.Session!, args.Source.Id, args.Source.Settings?.Values, args.Gallery!, AppServices.PageCache);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The saved copy cannot be opened (moved, damaged): back to where the user came from.
            MainWindow.Instance?.GoBack();
            return;
        }
        var library = await AppServices.Library;
        var remembered = await library.ReaderSettings.GetAsync(provider.Key);
        var needsCover = (await library.History.GetAsync(provider.Key))?.CoverFile is null;
        await library.History.TouchAsync(provider.Key, provider.Title, provider.Sizes.Count, library.Now);
        if (_args != args)
        {
            provider.Dispose();   // another gallery was opened meanwhile
            return;
        }
        _session = new ReaderSession(provider, preferences.ReadingWidth)
        {
            PreloadPages = Math.Clamp(preferences.PreloadPages, 3, 5),
        };
        _session.PageSizeChanged += OnPageSizeChanged;
        if (needsCover)
        {
            var key = provider.Key;
            void OnBytes(int page, byte[] bytes)
            {
                if (page != 0 || _session is not { } session) return;
                session.PageBytes -= OnBytes;
                AppServices.Track(HistoryCovers.SaveAsync(key, bytes));
            }
            _session.PageBytes += OnBytes;
            // Opened further in, the first page may never load: fetch just that page for the cover
            // (saved copy, page cache, or one download through the source).
            if (args.StartPage > 0 && provider.Sizes.Count > 0) AppServices.Track(CoverFromFirstPageAsync(provider));
        }
        _session.Limited += OnLimited;
        Layout.Geometry = _session.Geometry;
        Strip.ItemsSource = _session.Pages;
        _positioned = false;
        _reachedEnd = false;
        _jumpY = double.NaN;
        // The gallery's own zoom, width mode and gaps when it was read before (FR-020).
        _zoom = remembered?.Zoom ?? 1;
        _fitWidth = remembered?.FitWidth ?? preferences.FitWidth;
        // Webtoons are drawn as one continuous strip: they start without gaps.
        _gaps = preferences.PageGap > 0 && (remembered?.Gaps ?? !IsWebtoon(args));
        _session.Geometry.SetGap(_gaps ? preferences.PageGap : 0);
        GapButton.IsChecked = _gaps;
        GapButton.IsEnabled = preferences.PageGap > 0;
        TitleText.Text = _session.Title;
        Scrubber.Maximum = Math.Max(1, _session.Pages.Count);
        Scrubber.Visibility = _session.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateZoomText();
        LimitBar.IsOpen = false;
        ShowChrome(true);
        ShowSave();
        // The viewport may already have its size (no SizeChanged will come): lay out now.
        if (Scroller.ViewportWidth > 0) Scroller_SizeChanged(Scroller, null!);
    }

    private static async Task CoverFromFirstPageAsync(IPageProvider provider)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var bytes = await provider.LoadAsync(0, new PageState(0, provider.Sizes.Count), null, timeout.Token);
            await HistoryCovers.SaveAsync(provider.Key, bytes);
        }
        catch (Exception)
        {
            // No cover this time (offline, reader closed, source error); the next opening tries again.
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopGlide();
        _saveTimer.Stop();
        _hideTimer.Stop();
        SaveProgress(writeArchives: true);
        if (MainWindow.Instance is { } window)
        {
            window.FullScreen.Changed -= OnFullScreenChanged;
            window.NavigationPaneVisible = true;
        }
        _session?.Dispose();
        _session = null;
        _args = null;
        Strip.ItemsSource = null;
    }

    /// <summary>Keys scroll the strip, so it gets the focus (not the first toolbar button).</summary>
    public Control InitialFocus => Scroller;

    private double ViewportWidth => Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth;

    private double ColumnWidth => StripGeometry.ColumnWidthFor(ViewportWidth, AppServices.Preferences.ReadingWidth, _zoom, _fitWidth);

    private static bool IsWebtoon(Args args) => args.Saved is { } saved
        ? saved.Archive.Tags.Any(t => string.Equals(t.Name, "webtoon", StringComparison.OrdinalIgnoreCase))
        : (args.Gallery?["tags"] as JsonArray ?? []).Any(t => string.Equals(t?["name"]?.GetValue<string>(), "webtoon", StringComparison.OrdinalIgnoreCase));

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_session is null || Scroller.ViewportWidth <= 0) return;
        _session.Scale = XamlRoot?.RasterizationScale ?? 1;
        var position = _positioned ? _session.Geometry.PositionAt(ScrollY) : (_args!.StartPage, _args.StartOffset);
        Relayout(position, 0);
        _positioned = true;
        if (FocusState == FocusState.Unfocused) Scroller.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Applies the current column width and puts <paramref name="position"/> at <paramref name="screenY"/>;
    /// <paramref name="across"/> gives the horizontal offset for the new width (one view change for both,
    /// a second one would cancel the first).
    /// </summary>
    private void Relayout((int Page, double Offset) position, double screenY, Func<double, double>? across = null)
    {
        StopGlide();
        _session!.SetColumnWidth(ColumnWidth);
        Layout.Refresh();
        Strip.UpdateLayout();
        ScrollTo(_session.Geometry.ScrollFor(position.Page, position.Offset) - screenY, x: across?.Invoke(_session.Geometry.ColumnWidth));
        // ChangeView to an unchanged offset raises no ViewChanged, so plan explicitly.
        Plan();
        UpdatePageIndicator();
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            _wheelTarget = double.NaN;
            _acrossTarget = double.NaN;
            _jumpY = double.NaN;
        }
        Plan();
        if (_session is null || !_positioned) return;
        if (ScrollY + Scroller.ViewportHeight >= _session.Geometry.TotalHeight - 1) _reachedEnd = true;
        UpdatePageIndicator();
        // Saved 5 s after scrolling stops (FR-018).
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Page and fraction of it at the top of the view (read once the last page was fully in view) and
    /// the reader settings, into the library; saved copies get them in their archive (FR-009, FR-020).
    /// </summary>
    public void SaveProgress(bool writeArchives = false)
    {
        if (_session is null || !_positioned || _session.Pages.Count == 0) return;
        var (page, offset) = _session.Geometry.PositionAt(ScrollY);
        var key = _session.Key;
        var now = DateTimeOffset.UtcNow;
        var progress = new Progress(page, offset, _session.Pages.Count, _reachedEnd, now);
        var settings = new ReaderSettings(_zoom, _fitWidth, _gaps, now);
        AppServices.Track(Task.Run(async () =>
        {
            var library = await AppServices.Library;
            await library.SaveReadingAsync(key, progress, settings);
            if (writeArchives) await library.FlushMetadataAsync();
        }));
    }

    private void Plan() => _session?.UpdateView(ScrollY, Scroller.ViewportHeight);

    /// <summary>
    /// An exact size arrived. A page entirely above the view shifts the scroll offset by exactly
    /// its height change, so the content in view stays where it is (FR-006, research R3).
    /// </summary>
    private void OnPageSizeChanged(int page, double delta)
    {
        if (_session is null) return;
        // While gliding, the glide position is ahead of the reported offset.
        var offset = ScrollY;
        var wasAbove = _session.Geometry.TopOf(page) + _session.Geometry.HeightOf(page) - delta <= offset + 0.5;
        Layout.Refresh();
        Strip.UpdateLayout();
        if (!wasAbove || delta == 0) return;
        if (_gliding && !_glideAcross) _glideY += delta;
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
    /// <summary>Left/Right: in window widths per second (the column is wider than the window then).</summary>
    private static readonly GlideProfile AcrossGlide = new(1.2, 10, 6);

    private GlideProfile _glideProfile = ArrowGlide;
    private VirtualKey _glideKey;
    private int _glideDirection;
    private double _glideVelocity;
    private double _glideY;
    private double _glideX;
    private bool _glideAcross;
    private TimeSpan _glideLast;
    private bool _gliding;

    /// <summary>
    /// Key repeats come only ~30 times a second and each restarted a short scroll animation, so a
    /// held key moved the strip in jerks. Instead the strip moves every frame while the key is
    /// down: the speed eases in, and after release it eases out (inertia). Page keys and Space are
    /// the same, only faster: no fixed page-sized steps, so a short press never springs back.
    /// Left/Right glide across the same way (<paramref name="across"/>).
    /// </summary>
    private void StartGlide(VirtualKey key, int direction, GlideProfile profile, bool across = false)
    {
        if (_gliding && _glideAcross != across) StopGlide();
        if (!_gliding)
        {
            _gliding = true;
            _glideAcross = across;
            _glideY = ScrollY;
            _glideX = Scroller.HorizontalOffset;
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
        var top = p.Speed * (_glideAcross ? Scroller.ViewportWidth : _session?.Geometry.ColumnWidth ?? Scroller.ViewportWidth);
        // Key released (also when the window lost the key-up).
        if (_glideDirection != 0 && !InputKeyboardSource.GetKeyStateForCurrentThread(_glideKey).HasFlag(CoreVirtualKeyStates.Down)) _glideDirection = 0;

        var target = _glideDirection * top;
        var rate = _glideDirection != 0 ? p.EaseIn : p.EaseOut;
        _glideVelocity += (target - _glideVelocity) * Math.Min(1, dt * rate);

        double position, max;
        if (_glideAcross)
        {
            max = Scroller.ScrollableWidth;
            position = _glideX = Math.Clamp(_glideX + _glideVelocity * dt, 0, max);
            Scroller.ChangeView(_glideX, null, null, disableAnimation: true);
        }
        else
        {
            max = Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight);
            position = _glideY = Math.Clamp(_glideY + _glideVelocity * dt, 0, max);
            Scroller.ChangeView(null, _glideY, null, disableAnimation: true);
        }
        var settled = _glideDirection == 0 && Math.Abs(_glideVelocity) < 20;
        var atEdge = position <= 0 && _glideVelocity < 0 || position >= max && _glideVelocity > 0;
        if (settled || atEdge) StopGlide();
    }

    /// <summary>
    /// Offset of a jump ChangeView has taken but VerticalOffset does not show yet (NaN: none). Sizes
    /// arriving in between were measured from the old offset and the compensation was lost, so a
    /// reader opened mid-gallery drifted by the size errors of the pages above it.
    /// </summary>
    private double _jumpY = double.NaN;

    /// <summary>Where the view is or is about to be: glide position, pending jump, or the actual offset.</summary>
    private double ScrollY => _gliding && !_glideAcross ? _glideY : double.IsNaN(_jumpY) ? Scroller.VerticalOffset : _jumpY;

    /// <param name="x">Horizontal offset to go to as well (null: unchanged).</param>
    private void ScrollTo(double y, bool animate = false, double? x = null)
    {
        y = Math.Max(0, y);
        // An unchanged offset raises no ViewChanged, which would leave the jump pending forever.
        _jumpY = !animate && Math.Abs(y - Scroller.VerticalOffset) >= 0.5 ? y : double.NaN;
        Scroller.ChangeView(x is { } h ? Math.Max(0, h) : null, y, null, disableAnimation: !animate);
    }

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
        var page = _session.Geometry.PageAt(ScrollY + Scroller.ViewportHeight / 3) + 1;
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

    /// <summary>
    /// Changes the column width, keeping the strip point under (<paramref name="screenX"/>,
    /// <paramref name="screenY"/>) in place (FR-015) — across too, once the page is wider than the
    /// window (it used to stick to the left edge). Default: the middle of the view.
    /// </summary>
    private void SetZoom(double zoom, double screenY, double? screenX = null)
    {
        if (_session is null) return;
        zoom = Math.Clamp(Math.Round(zoom, 2), MinZoom, MaxZoom);
        if (zoom == _zoom) return;
        var x = screenX ?? Scroller.ViewportWidth / 2;
        var anchor = _session.Geometry.PositionAt(ScrollY + screenY);
        var oldWidth = _session.Geometry.ColumnWidth;
        var across = (Scroller.HorizontalOffset + x - ColumnLeft(oldWidth)) / oldWidth;
        _zoom = zoom;
        Relayout(anchor, screenY, width => across * width + ColumnLeft(width) - x);
        UpdateZoomText();
    }

    /// <summary>Left edge of the column in the scrolled content: centred while narrower than the view.</summary>
    private double ColumnLeft(double width) => Math.Max(0, (Scroller.ViewportWidth - width) / 2);

    private double _acrossTarget = double.NaN;

    /// <summary>
    /// Across the strip when it is wider than the window (arrows, Shift+wheel, tilt wheel). Steps add up
    /// to one target while the previous animation still runs, so a turned wheel glides instead of jerking.
    /// </summary>
    private void ScrollAcross(double delta)
    {
        if (Scroller.ScrollableWidth <= 0) return;
        var from = double.IsNaN(_acrossTarget) ? Scroller.HorizontalOffset : _acrossTarget;
        _acrossTarget = Math.Clamp(from + delta, 0, Scroller.ScrollableWidth);
        Scroller.ChangeView(_acrossTarget, null, null, disableAnimation: false);
    }

    /// <summary>
    /// Shift+wheel and the tilt wheel go across. Handled on the strip, before the scroll viewer: it
    /// scrolls down on any plain wheel turn and marks the event handled, so its own handler never saw it.
    /// </summary>
    private void Strip_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scroller);
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;   // zoom: the scroll viewer's handler
        if (!point.Properties.IsHorizontalMouseWheel && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)) return;
        var delta = point.Properties.MouseWheelDelta;
        // The tilt wheel reports right as positive; Shift+wheel down (negative) also goes right.
        var right = point.Properties.IsHorizontalMouseWheel ? delta : -delta;
        ScrollAcross(right / 120.0 * WheelStep * Scroller.ViewportWidth);
        e.Handled = true;
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
            var anchor = _session.Geometry.PositionAt(ScrollY + Scroller.ViewportHeight / 2);
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
        var anchor = _session.Geometry.PositionAt(ScrollY + Scroller.ViewportHeight / 2);
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
        SetZoom(_zoom + Math.Sign(point.Properties.MouseWheelDelta) * ZoomStep, point.Position.Y, point.Position.X);
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
            SetZoom(_zoom + Math.Sign(notches) * ZoomStep, point.Position.Y, point.Position.X);
            return;
        }
        StopGlide();
        // Successive notches add up even while the previous animation is still running.
        var from = double.IsNaN(_wheelTarget) ? ScrollY : _wheelTarget;
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

    // --- save to library (006 US1): online galleries only ------------------------------------------

    private SaveInfo? _save;

    private void OnSaveChanged(GalleryKey key)
    {
        if (_session?.Key == key) DispatcherQueue.TryEnqueue(ShowSave);
    }

    private async void ShowSave()
    {
        if (_session is not { } session || _args?.Saved is not null)
        {
            SaveButton.Visibility = Visibility.Collapsed;
            return;
        }
        var info = await SaveTracker.GetAsync(session.Key);
        if (_session != session) return;
        _save = info;
        SaveButton.Visibility = Visibility.Visible;
        SaveIcon.Glyph = info.IsSaved ? "" : info.Download is null ? "" : "";   // Library / Download / Pause
        ToolTipService.SetToolTip(SaveButton, info.Text);
        AutomationProperties.SetName(SaveButton, info.Text);
        AutomationProperties.SetItemStatus(SaveButton, info.Download?.State.ToString() ?? (info.IsSaved ? "Saved" : "NotSaved"));
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_save is null || _args is null) return;
        // Already saved: the reader stays online; In library is shown on the gallery screen.
        await SaveTracker.ClickAsync(_save, _args.Source, _args.Gallery);
        ShowSave();
    }

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
        if (_args?.Source?.LastAccountStatus?["imageLimit"] is JsonObject limit)
            LimitBar.Message = message + " " + Strings.Format("Account_ImageLimit", limit["used"]!.GetValue<double>(), limit["max"]!.GetValue<double>());
    }

    private void LimitRetryButton_Click(object sender, RoutedEventArgs e)
    {
        LimitBar.IsOpen = false;
        _session?.RetryAfterLimit();
        Plan();
    }

    private Windows.Foundation.Point? _lastPointer;

    /// <summary>
    /// The toolbar and scrubber return when the pointer moves near the top or right edge (FR-017).
    /// Only a real move counts: hiding the toolbar under a still pointer raises PointerMoved too, which
    /// brought it straight back although nobody was reaching for it.
    /// </summary>
    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Root).Position;
        var moved = _lastPointer is not { } last || Math.Abs(p.X - last.X) >= 1 || Math.Abs(p.Y - last.Y) >= 1;
        _lastPointer = p;
        if (moved && (p.Y < 80 || p.X > Root.ActualWidth - 80)) ShowChrome(true);
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
        if (e.Key is VirtualKey.Left or VirtualKey.Right && Scroller.ScrollableWidth > 0)
        {
            // Glides like Up/Down: the first repeat comes only after a pause, and repeats step unevenly.
            if (!e.KeyStatus.WasKeyDown) StartGlide(e.Key, e.Key == VirtualKey.Right ? 1 : -1, AcrossGlide, across: true);
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
