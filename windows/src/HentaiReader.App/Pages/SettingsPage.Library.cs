using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Data;
using HentaiReader.Host.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HentaiReader.App.Pages;

/// <summary>Settings added by 006: start page, library folders, page cache folder, default searches.</summary>
public sealed partial class SettingsPage
{
    // --- start page (FR-021) -----------------------------------------------------------------------

    private void BuildStartPage(Args args)
    {
        StartPageBox.Header = Strings.Get("Settings_StartPage");
        var options = new List<Option> { new(Preferences.StartLastSource, Strings.Get("Settings_StartLastSource")) };
        options.AddRange(args.Sources.Where(s => s.IsAvailable).Select(s => new Option(Preferences.StartSourcePrefix + s.Id, s.Name)));
        options.Add(new Option(Preferences.StartHistory, Strings.Get("Nav_History")));
        options.Add(new Option(Preferences.StartLibrary, Strings.Get("Nav_Library")));
        StartPageBox.SelectionChanged -= StartPageBox_SelectionChanged;
        StartPageBox.ItemsSource = options;
        StartPageBox.SelectedItem = options.FirstOrDefault(o => o.Tag == args.Preferences.StartPage) ?? options[0];
        StartPageBox.SelectionChanged += StartPageBox_SelectionChanged;
    }

    private void StartPageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_args is null || StartPageBox.SelectedItem is not Option option) return;
        _args.Preferences.StartPage = option.Tag;
        _args.Preferences.Save();
    }

    // --- library folders (FR-015–FR-017) -------------------------------------------------------------

    private async Task BuildLibraryAsync()
    {
        LibraryHeader.Text = Strings.Get("Settings_Library");
        var library = await AppServices.Library;
        var folders = new LibraryFolders(library, RecycleBin.Delete);
        var list = await folders.ListAsync();
        LibraryPanel.Children.Clear();
        foreach (var folder in list)
        {
            var count = await library.Archives.CountAsync(folder.Id);
            LibraryPanel.Children.Add(FolderRow(folders, folder, count, list.Count > 1));
        }
        var add = new Button { Content = Strings.Get("Settings_AddFolder") };
        AutomationProperties.SetAutomationId(add, "AddLibraryFolder");
        add.Click += async (_, _) =>
        {
            if (await PickFolderAsync() is not { } path) return;
            await folders.AddAsync(path);
            await RefreshLibraryAsync();
        };
        LibraryPanel.Children.Add(add);
    }

    private FrameworkElement FolderRow(LibraryFolders folders, LibraryFolder folder, int count, bool removable)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var target = new RadioButton { GroupName = "LibraryTarget", IsChecked = folder.IsTarget, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(target, Strings.Get("Settings_TargetFolder"));
        AutomationProperties.SetName(target, Strings.Get("Settings_TargetFolder"));
        target.Checked += async (_, _) => await folders.SetTargetAsync(folder.Id);
        grid.Children.Add(target);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = folder.Path, TextTrimming = TextTrimming.CharacterEllipsis });
        var available = Directory.Exists(folder.Path);
        text.Children.Add(new TextBlock
        {
            Text = Strings.Plural("Settings_FolderGalleries", count) + (available ? "" : " · " + Strings.Get("Library_Unavailable")),
            Opacity = 0.7,
            FontSize = 12,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (removable)
        {
            var open = new Button { Content = Strings.Get("Library_OpenFolder") };
            AutomationProperties.SetAutomationId(open, "OpenLibraryFolder");
            open.Click += (_, _) => Explorer.OpenFolder(folder.Path);
            Grid.SetColumn(open, 2);
            grid.Children.Add(open);
            var remove = new Button { Content = Strings.Get("Settings_RemoveFolder") };
            remove.Click += async (_, _) => await RemoveFolderAsync(folders, folder, count, text);
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);
        }
        AutomationProperties.SetName(grid, folder.Path);
        return grid;
    }

    /// <summary>Move galleries into another folder, or keep the files and just forget the folder (FR-016).</summary>
    private async Task RemoveFolderAsync(LibraryFolders folders, LibraryFolder folder, int count, StackPanel status)
    {
        if (await folders.CannotRemoveAsync(folder.Id) == "downloads")
        {
            await Message(Strings.Get("Settings_RemoveFolder"), Strings.Get("Settings_FolderBusy"));
            return;
        }
        var others = (await folders.ListAsync()).Where(f => f.Id != folder.Id).ToList();
        var destination = new ComboBox { ItemsSource = others.Select(f => f.Path).ToList(), SelectedIndex = Math.Max(0, others.FindIndex(f => f.IsTarget)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Strings.Format("Settings_RemoveFolderBody", folder.Path, Strings.Plural("Settings_FolderGalleries", count)), TextWrapping = TextWrapping.Wrap });
        if (count > 0) body.Children.Add(destination);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("Settings_RemoveFolder"),
            Content = body,
            PrimaryButtonText = count > 0 ? Strings.Get("Settings_MoveGalleries") : Strings.Get("Settings_RemoveFolder"),
            SecondaryButtonText = count > 0 ? Strings.Get("Settings_KeepFiles") : "",
            CloseButtonText = Strings.Get("Reader_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        var choice = await dialog.ShowAsync();
        try
        {
            if (choice == ContentDialogResult.Primary && count > 0)
            {
                var progressText = new TextBlock { Opacity = 0.7, FontSize = 12 };
                status.Children.Add(progressText);
                var progress = new Progress<(int Done, int Total)>(p => progressText.Text = Strings.Format("Settings_MoveProgress", p.Done, p.Total));
                ConflictChoice? forAll = null;
                await folders.MoveAndRemoveAsync(folder.Id, others[destination.SelectedIndex].Id, progress, async conflict =>
                {
                    if (forAll is { } all) return all;
                    var (choice, remember) = await AskConflictAsync(conflict);
                    if (remember) forAll = choice;
                    return choice;
                });
            }
            else if (choice == ContentDialogResult.Primary || choice == ContentDialogResult.Secondary)
            {
                await folders.RemoveKeepFilesAsync(folder.Id);
            }
            else
            {
                return;
            }
        }
        catch (Exception e)
        {
            await Message(Strings.Get("Settings_RemoveFolder"), e.Message);
        }
        await RefreshLibraryAsync();
    }

    /// <summary>The destination has this gallery or file name with different pages: which copy stays (FR-016).</summary>
    private async Task<(ConflictChoice Choice, bool ForAll)> AskConflictAsync(MoveConflict conflict)
    {
        static string Describe(string label, ArchiveSummary a) =>
            $"{label}: {a.Path}\n{Strings.Format("Settings_MoveConflictDetails", a.PageCount, a.Size / (1024.0 * 1024.0), a.ModifiedAt.ToLocalTime())}";
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = Strings.Format("Settings_MoveConflictBody", conflict.Incoming.Title), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = Describe(Strings.Get("Settings_MoveConflictExisting"), conflict.Existing), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        body.Children.Add(new TextBlock { Text = Describe(Strings.Get("Settings_MoveConflictIncoming"), conflict.Incoming), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        // Choices as radio buttons: Escape must not discard anything, so it means "keep both".
        var options = new RadioButtons { SelectedIndex = 0 };
        options.Items.Add(Strings.Get("Settings_MoveKeepBoth"));
        options.Items.Add(Strings.Get("Settings_MoveReplace"));
        options.Items.Add(Strings.Get("Settings_MoveKeepExisting"));
        body.Children.Add(options);
        var all = new CheckBox { Content = Strings.Get("Settings_MoveConflictForAll") };
        body.Children.Add(all);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings.Get("Settings_MoveConflictTitle"),
            Content = new ScrollViewer { Content = body },
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        var choice = result != ContentDialogResult.Primary ? ConflictChoice.KeepBoth : options.SelectedIndex switch
        {
            1 => ConflictChoice.Replace,
            2 => ConflictChoice.KeepExisting,
            _ => ConflictChoice.KeepBoth,
        };
        return (choice, all.IsChecked == true);
    }

    private async Task RefreshLibraryAsync()
    {
        if (AppServices.Watcher is { } watcher) await watcher.RefreshAsync();
        SaveTracker.RaiseChanged(default);
        await BuildLibraryAsync();
    }

    // --- page cache folder (FR-024, FR-025) ----------------------------------------------------------

    private TextBlock? _cacheMoveText;
    private Button? _cancelMove;

    private void BuildCacheFolder()
    {
        CacheFolderPanel.Children.Clear();
        var cache = AppServices.PageCache;
        // Button on the right edge, like the library folder rows.
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = Strings.Format("Settings_CacheFolder", cache.Folder), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var change = new Button { Content = Strings.Get("Settings_ChangeFolder"), IsEnabled = cache.MovingFrom is null };
        Grid.SetColumn(change, 1);
        AutomationProperties.SetAutomationId(change, "ChangeCacheFolder");
        change.Click += async (_, _) =>
        {
            if (await PickFolderAsync() is not { } path) return;
            try
            {
                AppServices.MoveCache(Path.Combine(path, "Hentai Reader cache"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await Message(Strings.Get("Settings_ChangeFolder"), e.Message);
            }
            BuildCacheFolder();
        };
        row.Children.Add(change);
        CacheFolderPanel.Children.Add(row);
        if (cache.MovingFrom is { } from)
        {
            var (remaining, total) = cache.MoveProgress;
            _cacheMoveText = new TextBlock { Text = Strings.Format("Settings_CacheMoving", total - remaining, total), Opacity = 0.7 };
            _cancelMove = new Button { Content = Strings.Get("Settings_CancelMove") };
            _cancelMove.Click += (_, _) =>
            {
                AppServices.MoveCache(from);   // back to the previous folder
                BuildCacheFolder();
            };
            var moving = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            moving.Children.Add(_cacheMoveText);
            moving.Children.Add(_cancelMove);
            CacheFolderPanel.Children.Add(moving);
        }
        AppServices.CacheMoveChanged -= OnCacheMoveChanged;
        AppServices.CacheMoveChanged += OnCacheMoveChanged;
    }

    private void OnCacheMoveChanged((int Moved, int Total)? progress, string? error) => DispatcherQueue.TryEnqueue(() =>
    {
        if (error is not null && _cacheMoveText is not null) _cacheMoveText.Text = Strings.Format("Settings_CacheMoveFailed", error);
        else if (progress is { } p && _cacheMoveText is not null) _cacheMoveText.Text = Strings.Format("Settings_CacheMoving", p.Moved, p.Total);
        else if (progress is null && error is null) BuildCacheFolder();
    });

    // --- default searches (FR-023) -------------------------------------------------------------------

    private FrameworkElement? BuildDefaultQuery(SourceEntry source)
    {
        if (_args?.Preferences.DefaultQueries.GetValueOrDefault(source.Id) is not { } query) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var summary = string.Join(" · ", new[] { query.Text }.Concat(query.Filters.Select(f => $"{Strings.FilterLabel(f.Key)}: {f.Value?.ToJsonString().Trim('"')}")).Where(t => t.Length > 0));
        row.Children.Add(new TextBlock { Text = Strings.Format("Settings_DefaultSearch", summary.Length > 0 ? summary : Strings.Get("Settings_DefaultSearchEmpty")), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        var remove = new Button { Content = Strings.Get("Settings_RemoveDefaultSearch") };
        remove.Click += (_, _) =>
        {
            _args.Preferences.DefaultQueries.Remove(source.Id);
            _args.Preferences.Save();
            row.Visibility = Visibility.Collapsed;
        };
        row.Children.Add(remove);
        return row;
    }

    // --- helpers -------------------------------------------------------------------------------------

    private async Task<string?> PickFolderAsync()
    {
        if (MainWindow.Instance is not { } window) return null;
        var picker = new FolderPicker(window.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    private async Task Message(string title, string text) =>
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK" }.ShowAsync();
}



