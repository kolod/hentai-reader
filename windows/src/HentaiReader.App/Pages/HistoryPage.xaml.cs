using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Data;
using HentaiReader.Host.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;

namespace HentaiReader.App.Pages;

/// <summary>
/// Galleries opened in the reader, newest first (US2): open continues at the saved page with the
/// gallery's own zoom (saved copy first, else online), remove one, clear all.
/// </summary>
public sealed partial class HistoryPage : Page
{
    private HistoryCollection? _items;
    private HistoryEntry? _failed;
    // Missing covers, made one at a time for the rows that came into view.
    private readonly Queue<HistoryEntry> _coverQueue = new();
    private readonly HashSet<GalleryKey> _coverTried = new();
    private CancellationTokenSource _coverCts = new();
    private bool _coverRunning;

    public HistoryPage()
    {
        InitializeComponent();
        TitleText.Text = Strings.Get("Nav_History");
        ClearButton.Content = Strings.Get("History_Clear");
        EmptyText.Text = Strings.Get("History_Empty");
        RemoveFailedButton.Content = Strings.Get("History_Remove");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ErrorBar.IsOpen = false;
        _coverCts = new CancellationTokenSource();
        _coverQueue.Clear();
        _coverTried.Clear();
        ShowList();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _coverCts.Cancel();

    private void QueueCover(HistoryEntry entry)
    {
        if (HistoryCovers.Has(entry) || !_coverTried.Add(entry.Key)) return;
        _coverQueue.Enqueue(entry);
        if (!_coverRunning) _ = MakeCoversAsync(_coverCts.Token);
    }

    private async Task MakeCoversAsync(CancellationToken token)
    {
        _coverRunning = true;
        try
        {
            while (!token.IsCancellationRequested && _coverQueue.TryDequeue(out var entry))
            {
                string? file;
                try
                {
                    file = await HistoryCovers.FindAsync(entry, AppServices.Preferences.NetworkCovers, token);
                }
                catch (Exception)
                {
                    file = null;   // offline, source error, closed page: the row stays without a cover
                }
                if (file is null || token.IsCancellationRequested || _items is null) continue;
                var index = _items.IndexOf(entry);
                if (index >= 0) _items[index] = entry with { CoverFile = file };   // the row is rebuilt with it
            }
        }
        finally
        {
            _coverRunning = false;
        }
    }

    private void ShowList()
    {
        _items = new HistoryCollection(this);
        HistoryList.ItemsSource = _items;
        // The first page is loaded here: the list asks for more only once it has been laid out.
        _ = _items.LoadMoreItemsAsync(0);
    }

    private sealed class HistoryCollection(HistoryPage owner) : ObservableCollection<HistoryEntry>, ISupportIncrementalLoading
    {
        private const int PageSize = 100;
        private bool _loading;
        public bool HasMoreItems { get; private set; } = true;

        public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) => Load().AsAsyncOperation();

        private async Task<LoadMoreItemsResult> Load()
        {
            if (_loading || !HasMoreItems) return new LoadMoreItemsResult { Count = 0 };
            _loading = true;
            try
            {
                var library = await AppServices.Library;
                var offset = Count;
                var page = await Task.Run(() => library.History.PageAsync(offset, PageSize));
                foreach (var entry in page) Add(entry);
                HasMoreItems = page.Count == PageSize;
                return new LoadMoreItemsResult { Count = (uint)page.Count };
            }
            catch (Exception e)
            {
                // A failed load used to leave the list silently empty.
                HasMoreItems = false;
                owner.ErrorBar.Title = Strings.Get("Nav_History");
                owner.ErrorBar.Message = $"{e.GetType().Name}: {e.Message}";
                owner.RemoveFailedButton.Visibility = Visibility.Collapsed;
                owner.ErrorBar.IsOpen = true;
                return new LoadMoreItemsResult { Count = 0 };
            }
            finally
            {
                _loading = false;
                owner.EmptyText.Visibility = Count == 0 && !owner.ErrorBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                owner.ClearButton.IsEnabled = Count > 0;
            }
        }
    }

    private void HistoryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not HistoryEntry entry || args.ItemContainer.ContentTemplateRoot is not Border slot) return;
        slot.Child = Row(entry);
        QueueCover(entry);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, entry.Title);
        var menu = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = Strings.Get("History_Remove"), Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += async (_, _) => await RemoveAsync(entry);
        menu.Items.Add(remove);
        args.ItemContainer.ContextFlyout = menu;
        args.Handled = true;
    }

    /// <summary>Cover · title · source and progress · time of last reading · library mark.</summary>
    private static FrameworkElement Row(HistoryEntry entry)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HistoryCovers.Width * 0.6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cover = new Border
        {
            Width = HistoryCovers.Width * 0.6,
            Height = HistoryCovers.Width * 0.9,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
        };
        if (HistoryCovers.Has(entry))
            cover.Child = new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(new Uri(HistoryCovers.PathOf(entry.CoverFile!))) };
        grid.Children.Add(cover);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        var source = entry.Key.SourceId == GalleryKey.LocalSource
            ? Strings.Get("Library_LocalSource")
            : MainWindow.Instance?.SourceNames.GetValueOrDefault(entry.Key.SourceId) ?? entry.Key.SourceId;
        var progress = entry.Progress switch
        {
            { Finished: true } => Strings.Get("Gallery_Finished"),
            { } p => Strings.Format("Gallery_Progress", p.Page + 1, Math.Max(p.PageCount, entry.PageCount)),
            null => Strings.Plural("Gallery_PageCount", entry.PageCount),
        };
        text.Children.Add(new TextBlock { Text = $"{source} · {progress}", Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = Strings.Relative(entry.LastReadAt), Opacity = 0.6, FontSize = 12 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (entry.Saved)
        {
            var mark = new FontIcon { Glyph = "", FontSize = 16, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(mark, Strings.Get("Save_InLibrary"));
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }
        return grid;
    }

    private async void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryEntry entry) await OpenAsync(entry);
    }

    private async void HistoryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || FocusManager.GetFocusedElement(XamlRoot) is not ListViewItem { Content: not null } container) return;
        if (HistoryList.ItemFromContainer(container) is HistoryEntry entry)
        {
            e.Handled = true;
            await RemoveAsync(entry);
        }
    }

    /// <summary>The saved copy when there is one, else online through the source (getGallery first).</summary>
    private async Task OpenAsync(HistoryEntry entry)
    {
        ErrorBar.IsOpen = false;
        var library = await AppServices.Library;
        var progress = await library.Progress.GetAsync(entry.Key);
        var (page, offset) = progress is { Finished: false } p ? (p.Page, p.Offset) : (0, 0.0);
        if (await library.FindSavedAsync(entry.Key) is { } saved)
        {
            Frame.Navigate(typeof(ReaderPage), ReaderPage.Args.Offline(saved, page, offset));
            return;
        }
        if (MainWindow.Instance?.FindSource(entry.Key.SourceId) is not { IsAvailable: true } source)
        {
            ShowError(entry, Strings.Get("History_SourceUnavailable"));
            return;
        }
        Busy.IsActive = true;
        var result = await source.CallAsync("getGallery", [JsonValue.Create(entry.Key.GalleryId)]);
        Busy.IsActive = false;
        if (!result.Ok)
        {
            var kind = result.Error!.Type == "SourceError" ? result.Error.Kind! : "ScriptError";
            ShowError(entry, Strings.ErrorBody(kind), Strings.ErrorTitle(kind), kind == SourceErrorKind.NotFound);
            return;
        }
        Frame.Navigate(typeof(ReaderPage), ReaderPage.Args.Online(source, result.Value!.AsObject(), page, offset));
    }

    private void ShowError(HistoryEntry entry, string message, string? title = null, bool offerRemove = true)
    {
        _failed = entry;
        ErrorBar.Title = title ?? entry.Title;
        ErrorBar.Message = message;
        RemoveFailedButton.Visibility = offerRemove ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.IsOpen = true;
    }

    private async void RemoveFailedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_failed is { } entry) await RemoveAsync(entry);
        ErrorBar.IsOpen = false;
    }

    private async Task RemoveAsync(HistoryEntry entry)
    {
        var library = await AppServices.Library;
        HistoryCovers.Delete(await library.History.RemoveAsync(entry.Key));
        _items?.Remove(entry);
        EmptyText.Visibility = _items is { Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("History_ClearTitle"),
            Content = Strings.Get("History_ClearBody"),
            PrimaryButtonText = Strings.Get("History_Clear"),
            CloseButtonText = Strings.Get("Reader_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var library = await AppServices.Library;
        foreach (var file in await library.History.ClearAsync()) HistoryCovers.Delete(file);
        ShowList();
    }
}
