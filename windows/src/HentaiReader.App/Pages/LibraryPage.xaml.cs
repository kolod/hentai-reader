using HentaiReader.App.Controls;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HentaiReader.App.Pages;

/// <summary>
/// Saved galleries of all library folders (US1, US3): search by title, source and read filters, sorts,
/// open in the reader at the saved position, open the folder, delete to the Recycle Bin.
/// </summary>
public sealed partial class LibraryPage : Page
{
    private sealed record Option(object? Value, string Text);

    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _ready;
    private int _version;
    private bool _manyFolders;

    public LibraryPage()
    {
        InitializeComponent();
        TitleText.Text = Strings.Get("Nav_Library");
        SearchBox.PlaceholderText = Strings.Get("Library_Search");
        FoldersButton.Content = Strings.Get("Library_FoldersButton");
        ReadBox.ItemsSource = new[]
        {
            new Option(ReadFilter.All, Strings.Get("Library_FilterAll")),
            new Option(ReadFilter.Unread, Strings.Get("Library_Unread")),
            new Option(ReadFilter.InProgress, Strings.Get("Library_InProgress")),
            new Option(ReadFilter.Finished, Strings.Get("Gallery_Finished")),
        };
        SortBox.ItemsSource = new[]
        {
            new Option(LibrarySort.Added, Strings.Get("Library_SortAdded")),
            new Option(LibrarySort.LastRead, Strings.Get("Library_SortLastRead")),
            new Option(LibrarySort.Title, Strings.Get("Library_SortTitle")),
        };
        ReadBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = ReloadAsync();
        };
        Loaded += (_, _) => SaveTracker.Changed += OnLibraryChanged;
        Unloaded += (_, _) => SaveTracker.Changed -= OnLibraryChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var sources = new List<Option> { new(null, Strings.Get("Library_FilterAllSources")) };
        sources.AddRange((MainWindow.Instance?.SourceNames ?? new Dictionary<string, string>()).Select(s => new Option(s.Key, s.Value)));
        sources.Add(new Option(GalleryKey.LocalSource, Strings.Get("Library_LocalSource")));
        var selected = (SourceBox.SelectedItem as Option)?.Value;
        SourceBox.ItemsSource = sources;
        SourceBox.SelectedItem = sources.FirstOrDefault(o => Equals(o.Value, selected)) ?? sources[0];
        _ready = true;
        _ = ReloadAsync();
    }

    private void OnLibraryChanged(GalleryKey key) => DispatcherQueue.TryEnqueue(() => _ = ReloadAsync());

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var version = ++_version;
        var query = new LibraryQuery(
            Text: SearchBox.Text,
            SourceId: (SourceBox.SelectedItem as Option)?.Value as string,
            Read: (ReadBox.SelectedItem as Option)?.Value is ReadFilter r ? r : ReadFilter.All,
            Sort: (SortBox.SelectedItem as Option)?.Value is LibrarySort s ? s : LibrarySort.Added,
            Limit: 10_000);
        var library = await AppServices.Library;
        var items = await Task.Run(() => library.Archives.ListAsync(query));
        var folders = await Task.Run(() => library.Folders.ListAsync());
        if (version != _version) return;
        _manyFolders = folders.Count > 1;
        LibraryGrid.ItemsSource = items.ToList();
        var empty = items.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        var filtered = !string.IsNullOrWhiteSpace(query.Text) || query.SourceId is not null || query.Read != ReadFilter.All;
        EmptyText.Text = Strings.Get(filtered ? "Library_NoMatches" : "Library_Empty");
        FoldersButton.Visibility = filtered ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(LibraryGrid, items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void LibraryGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not LibraryItem item) return;
        if (args.ItemContainer.ContentTemplateRoot is not LibraryCard card) return;
        var name = item.Archive.Key.SourceId == GalleryKey.LocalSource
            ? Strings.Get("Library_LocalSource")
            : MainWindow.Instance?.SourceNames.GetValueOrDefault(item.Archive.Key.SourceId) ?? item.Archive.Key.SourceId;
        card.Show(item, name, _manyFolders);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, item.Archive.Title);
        args.ItemContainer.ContextFlyout = Menu(item);
        args.Handled = true;
    }

    private MenuFlyout Menu(LibraryItem item)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = Strings.Get("Library_OpenFolder"), Icon = new SymbolIcon(Symbol.OpenLocal) };
        open.Click += (_, _) => OpenFolder(item);
        var delete = new MenuFlyoutItem { Text = Strings.Get("Library_Delete"), Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteAsync(item);
        menu.Items.Add(open);
        menu.Items.Add(delete);
        return menu;
    }

    private async void LibraryGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibraryItem { Archive.State: ArchiveState.Ok } item) return;
        var progress = await (await AppServices.Library).Progress.GetAsync(item.Archive.Key);
        Frame.Navigate(typeof(ReaderPage), ReaderPage.Args.Offline(item, progress is { Finished: false } p ? p.Page : 0, progress is { Finished: false } q ? q.Offset : 0));
    }

    private static void OpenFolder(LibraryItem item)
    {
        if (File.Exists(item.FullPath)) Explorer.ShowFile(item.FullPath);
        else Explorer.OpenFolder(item.FolderPath);
    }

    /// <summary>Asks, then moves the archive to the Recycle Bin; history and progress stay (FR-014).</summary>
    private async Task DeleteAsync(LibraryItem item)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("Library_DeleteTitle"),
            Content = new TextBlock { Text = Strings.Format("Library_DeleteBody", item.Archive.Title), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Strings.Get("Library_Delete"),
            CloseButtonText = Strings.Get("Reader_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (File.Exists(item.FullPath)) await Task.Run(() => RecycleBin.Delete(item.FullPath));
            var library = await AppServices.Library;
            await library.Archives.DeleteAsync(item.Archive.Id);
            SaveTracker.RaiseChanged(item.Archive.Key);
        }
        catch (IOException ex)
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = Strings.Get("Library_DeleteTitle"), Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    private void FoldersButton_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenSettings();
}
