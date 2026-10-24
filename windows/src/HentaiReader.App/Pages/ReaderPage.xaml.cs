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
        Label(FullScreenButton, "Reader_FullScreen");
        AutomationProperties.SetName(Scrubber, Strings.Get("Reader_Page"));
        LimitRetryButton.Content = Strings.Get("Search_Retry");

        // Dragging the scrubber previews the target; the jump happens on release.
        // The scroll viewer handles pointer moves; the toolbar still wants to see them.
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), handledEventsToo: true);
        Scrubber.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), handledEventsToo: true);
        Scrubber.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);
        Scrubber.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);
    }

    private static void Label(Button button, string key)
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
        Layout.Geometry = _session.Geometry;
        Strip.ItemsSource = _session.Pages;
        _positioned = false;
        _reachedEnd = false;
        _zoom = 1;
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
        }
        ShowChrome(true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _saveTimer.Stop();
        _hideTimer.Stop();
        SaveProgress();
        if (MainWindow.Instance is { } window)
        {
            window.Closed -= Window_Closed;
            window.FullScreen.Changed -= OnFullScreenChanged;
            // Other pages keep their navigation in full screen.
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

    private double ColumnWidth => StripGeometry.ColumnWidthFor(ViewportWidth, AppServices.Preferences.ReadingWidth, _zoom);

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
        var offset = Scroller.VerticalOffset;
        var wasAbove = _session.Geometry.TopOf(page) + _session.Geometry.HeightOf(page) - delta <= offset + 0.5;
        Layout.Refresh();
        Strip.UpdateLayout();
        if (wasAbove && delta != 0) ScrollTo(offset + delta);
    }

    private void ScrollTo(double y, bool animate = false) =>
        Scroller.ChangeView(null, Math.Max(0, y), null, disableAnimation: !animate);

    private void JumpToPage(int page)
    {
        if (_session is null) return;
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

    private void UpdateZoomText() => ZoomText.Text = Strings.Format("Reader_ZoomValue", (int)Math.Round(_zoom * 100));

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + ZoomStep, Scroller.ViewportHeight / 2);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - ZoomStep, Scroller.ViewportHeight / 2);

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        var point = e.GetCurrentPoint(Scroller);
        SetZoom(_zoom + Math.Sign(point.Properties.MouseWheelDelta) * ZoomStep, point.Position.Y);
        e.Handled = true;
    }

    // --- full screen and toolbar ------------------------------------------------------------------

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.FullScreen.Toggle();

    /// <summary>In the reader, full screen also hides the navigation pane (FR-016).</summary>
    private void OnFullScreenChanged(bool on)
    {
        if (MainWindow.Instance is { } window) window.NavigationPaneVisible = !on;
        FullScreenIcon.Symbol = on ? Symbol.BackToWindow : Symbol.FullScreen;
        Label(FullScreenButton, on ? "Reader_ExitFullScreen" : "Reader_FullScreen");
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.GoBack();

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
                    SetZoom(1, Scroller.ViewportHeight / 2);
                    break;
                default:
                    handled = false;
                    break;
            }
            e.Handled = handled;
            return;
        }

        var screen = Scroller.ViewportHeight * 0.9;
        var y = Scroller.VerticalOffset;
        double? target = e.Key switch
        {
            VirtualKey.Space => shift ? y - screen : y + screen,
            VirtualKey.PageDown => y + screen,
            VirtualKey.PageUp => y - screen,
            VirtualKey.Home => 0,
            VirtualKey.End => _session.Geometry.TotalHeight,
            VirtualKey.Down => y + Scroller.ViewportHeight * 0.1,
            VirtualKey.Up => y - Scroller.ViewportHeight * 0.1,
            _ => null,
        };
        if (target is { } t)
        {
            ScrollTo(t, animate: e.Key is not (VirtualKey.Home or VirtualKey.End));
            e.Handled = true;
        }
    }
}
