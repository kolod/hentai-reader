using System.ComponentModel;
using HentaiReader.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HentaiReader.App.Reader;

/// <summary>
/// One page of the strip (contracts/reader-ui.md "Page slot states"): a placeholder of the page's
/// expected size, the image stretched to exactly that box, a progress ring after 300 ms of loading,
/// or the error with Retry. The layout sizes the slot; the slot never sizes itself.
/// </summary>
public sealed class PageSlot : Grid
{
    private static readonly TimeSpan RingDelay = TimeSpan.FromMilliseconds(300);

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly ProgressRing _ring = new() { Width = 32, Height = 32, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly StackPanel _error = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 360 };
    private readonly Button _retry = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _ringTimer = new() { Interval = RingDelay };
    private PageState? _page;

    /// <summary>Retry of a failed page (handled by the reader page, which owns the session).</summary>
    public static event Action<PageState>? RetryRequested;

    public PageSlot()
    {
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        _retry.Content = Strings.Get("Search_Retry");
        _retry.Click += (_, _) =>
        {
            if (_page is not null) RetryRequested?.Invoke(_page);
        };
        _error.Children.Add(_errorText);
        _error.Children.Add(_retry);
        _ring.HorizontalAlignment = HorizontalAlignment.Center;
        _ring.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_image);
        Children.Add(_ring);
        Children.Add(_error);
        _ringTimer.Tick += (_, _) =>
        {
            _ringTimer.Stop();
            if (_page?.Load is PageLoad.Resolving or PageLoad.Downloading or PageLoad.Decoding)
            {
                _ring.Visibility = Visibility.Visible;
                _ring.IsActive = true;
            }
        };
        DataContextChanged += (_, _) => Bind(DataContext as PageState);
    }

    private void Bind(PageState? page)
    {
        if (_page is not null) _page.PropertyChanged -= Page_PropertyChanged;
        _page = page;
        if (page is not null)
        {
            page.PropertyChanged += Page_PropertyChanged;
            AutomationProperties.SetName(this, Strings.Format("Reader_PageName", page.Index + 1, page.Total));
        }
        Render();
    }

    private void Page_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        var page = _page;
        _image.Source = page?.Image;
        // Load state for UI Automation checks (tests read states, not page images).
        AutomationProperties.SetItemStatus(this, page is null ? "" : $"{page.Load}{(page.Image is null ? "" : "+image")}");
        var loading = page?.Load is PageLoad.Resolving or PageLoad.Downloading or PageLoad.Decoding;
        if (loading && _ring.Visibility == Visibility.Collapsed && !_ringTimer.IsEnabled) _ringTimer.Start();
        if (!loading)
        {
            _ringTimer.Stop();
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
        }
        var failed = page?.Load is PageLoad.Failed;
        _error.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        if (failed)
        {
            _errorText.Text = page!.Error is { } kind && kind != "Network"
                ? $"{Strings.ErrorTitle(kind)}. {Strings.Get("Reader_PageFailed")}"
                : Strings.Get("Reader_PageFailed");
        }
    }
}
