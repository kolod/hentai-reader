using HentaiReader.App.Localization;
using HentaiReader.App.Pages;
using HentaiReader.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HentaiReader.App;

/// <summary>
/// One window: the bundled sources as navigation items, settings in the footer. Sources are only
/// loaded after the adult-content confirmation, so no source can make a request before it (FR-021).
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The single main window (one instance per session).</summary>
    public static MainWindow? Instance { get; private set; }

    /// <summary>Borderless full screen, F11 on every page (FR-016).</summary>
    public FullScreen FullScreen { get; }

    private readonly Preferences _preferences;
    /// <summary>Navigation item tags of the History, Library and Downloads screens (006).</summary>
    public const string HistoryTag = "nav:history", LibraryTag = "nav:library", DownloadsTag = "nav:downloads";

    private readonly WindowPlacement _placement;
    private bool _syncingSelection;
    private readonly string? _startSourceId;
    private readonly SourceRegistry _sources = new();

    public MainWindow(Preferences preferences, string? startSourceId)
    {
        InitializeComponent();
        _preferences = preferences;
        _startSourceId = startSourceId;
        Title = Strings.Get("App_Title");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // The system title bar of an unpackaged window stays light; follow the app's light/dark mode.
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode;
        Nav.PaneTitle = Strings.Get("Nav_Sources");
        _placement = new WindowPlacement(AppWindow, preferences.Window);
        Closed += (_, _) =>
        {
            // Nothing is written before the adult content confirmation (see README, Data).
            if (_preferences.AdultConfirmed)
            {
                RememberPlacement();
                _preferences.Save();
            }
            // The reader stores its position before the library is closed.
            if (ContentFrame.Content is ReaderPage reader) reader.SaveProgress();
            _sources.Dispose();
            AppServices.Shutdown();
        };
        Instance = this;
        FullScreen = new FullScreen(AppWindow);
        InstallInput();
    }

    public async void StartAsync()
    {
        if (!_preferences.AdultConfirmed)
        {
            await WaitForContentAsync();
            if (!await Dialogs.AdultGateDialog.ConfirmAsync(Content.XamlRoot))
            {
                Application.Current.Exit();
                return;
            }
            _preferences.AdultConfirmed = true;
            _preferences.Save();
        }

        await AppServices.OpenLibraryAsync();
        _ = AppServices.StartWatcherAsync();
        AppServices.ContinueCacheMove();
        await _sources.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Sources"), _preferences);
        await AppServices.StartDownloadsAsync(id => _sources.Find(id) is { Session: { } session } entry
            ? new HentaiReader.Host.Downloads.DownloadSource(session, entry.Name, entry.Settings?.Values)
            : null);
        // History, Library and Downloads above the sources (FR-026).
        Nav.MenuItems.Add(new NavigationViewItem { Content = Strings.Get("Nav_History"), Tag = HistoryTag, Icon = new SymbolIcon(Symbol.Clock) });
        Nav.MenuItems.Add(new NavigationViewItem { Content = Strings.Get("Nav_Library"), Tag = LibraryTag, Icon = new FontIcon { Glyph = "" } });
        _downloadsItem = new NavigationViewItem { Content = Strings.Get("Nav_Downloads"), Tag = DownloadsTag, Icon = new SymbolIcon(Symbol.Download) };
        Nav.MenuItems.Add(_downloadsItem);
        Nav.MenuItems.Add(new NavigationViewItemSeparator());
        SaveTracker.Changed += _ => DispatcherQueue.TryEnqueue(UpdateDownloadsBadge);
        UpdateDownloadsBadge();
        foreach (var entry in _sources.Entries)
        {
            var item = new NavigationViewItem
            {
                Content = entry.Name,
                Tag = entry.Id,
                Icon = new SymbolIcon(Symbol.Library),
                IsEnabled = entry.IsAvailable,
            };
            if (!entry.IsAvailable) ToolTipService.SetToolTip(item, Strings.Format("Source_Unavailable", entry.UnavailableReason ?? ""));
            Nav.MenuItems.Add(item);
        }

        var start = _sources.Find(_startSourceId)?.IsAvailable == true ? _startSourceId : StartTag();
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == start);
    }

    /// <summary>
    /// Marks the navigation item of the page now shown, also after Back. Otherwise the item left
    /// selected (Settings after going back from it) could not be chosen again: clicking an
    /// already selected item raises no SelectionChanged.
    /// </summary>
    private void SyncSelection(object? parameter)
    {
        // The reader keeps the item it was opened from (a source, History or Library).
        if (ContentFrame.Content is ReaderPage) return;
        var sourceId = parameter switch
        {
            SourcePage.Args a => a.Source.Id,
            GalleryPage.Args a => a.Source.Id,
            _ => ContentFrame.Content switch
            {
                HistoryPage => HistoryTag,
                LibraryPage => LibraryTag,
                DownloadsPage => DownloadsTag,
                _ => null,
            },
        };
        object? item = ContentFrame.Content is SettingsPage ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == sourceId);
        if (item is null || ReferenceEquals(Nav.SelectedItem, item)) return;
        _syncingSelection = true;
        Nav.SelectedItem = item;
        _syncingSelection = false;
    }

    /// <summary>Display names of the loaded sources (id → name).</summary>
    public IReadOnlyDictionary<string, string> SourceNames => _sources.Entries.ToDictionary(e => e.Id, e => e.Name);

    public SourceEntry? FindSource(string id) => _sources.Find(id);

    /// <summary>Opens Settings (e.g. from the empty library).</summary>
    public void OpenSettings() => Nav.SelectedItem = Nav.SettingsItem;

    /// <summary>The navigation item to start on (FR-021): the start page setting, falling back to a source.</summary>
    private string? StartTag()
    {
        var available = _sources.Entries.Where(e => e.IsAvailable).Select(e => e.Id).ToList();
        var last = available.Contains(_preferences.LastSourceId ?? "") ? _preferences.LastSourceId : available.FirstOrDefault();
        return _preferences.StartPage switch
        {
            Preferences.StartHistory => HistoryTag,
            Preferences.StartLibrary => LibraryTag,
            { } page when page.StartsWith(Preferences.StartSourcePrefix, StringComparison.Ordinal) && available.Contains(page[Preferences.StartSourcePrefix.Length..])
                => page[Preferences.StartSourcePrefix.Length..],
            _ => last,
        };
    }

    private NavigationViewItem? _downloadsItem;

    /// <summary>Number of unfinished downloads on the Downloads item.</summary>
    private async void UpdateDownloadsBadge()
    {
        if (_downloadsItem is null || !AppServices.Library.IsCompletedSuccessfully) return;
        var rows = await AppServices.Library.Result.Downloads.ListAsync();
        var active = rows.Count(r => r.State is not HentaiReader.Host.Data.DownloadState.Done);
        _downloadsItem.InfoBadge = active > 0 ? new InfoBadge { Value = active } : null;
    }

    /// <summary>Puts the window bounds into the preferences (saved by the caller).</summary>
    public void RememberPlacement() => _preferences.Window = _placement.Current is { } bounds ? bounds with { FullScreen = FullScreen.IsOn } : null;

    /// <summary>
    /// Call after <c>Activate()</c>: restores the maximized state (a show before it would undo it), then
    /// full screen when the app was closed in it — entered last, so F11 returns to the restored window.
    /// </summary>
    public void ApplyPlacement()
    {
        _placement.ApplyAfterShow();
        if (_preferences.Window is { FullScreen: true }) FullScreen.Enter();
    }

    private Task WaitForContentAsync()
    {
        if (Content.XamlRoot is not null) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        ((FrameworkElement)Content).Loaded += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>
    /// Back-stack depth at which each section (a navigation item) began, innermost on top. Escape goes
    /// back only within the current section and closes the app on its first page, so a few presses
    /// always quit, however many sections were visited.
    /// </summary>
    private readonly Stack<int> _sectionStarts = new();

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.IsSettingsSelected)
        {
            NavigateSection(typeof(SettingsPage), new SettingsPage.Args(_preferences, _preferences.LastSourceId, _sources.Entries));
            return;
        }
        switch (args.SelectedItem)
        {
            case NavigationViewItem { Tag: HistoryTag }:
                NavigateSection(typeof(HistoryPage));
                return;
            case NavigationViewItem { Tag: LibraryTag }:
                NavigateSection(typeof(LibraryPage));
                return;
            case NavigationViewItem { Tag: DownloadsTag }:
                NavigateSection(typeof(DownloadsPage));
                return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: string id } && _sources.Find(id) is { IsAvailable: true } entry)
        {
            _preferences.LastSourceId = id;
            _preferences.Save();
            NavigateSection(typeof(SourcePage), new SourcePage.Args(entry, null));
        }
    }

    private void NavigateSection(Type page, object? parameter = null)
    {
        if (ContentFrame.Navigate(page, parameter)) _sectionStarts.Push(ContentFrame.BackStack.Count);
    }

    /// <summary>
    /// Focuses the page just shown, once it is in the tree: a page coming back from the cache (the
    /// gallery after the reader) is attached only after Navigated, so focusing at once did nothing,
    /// the reader's focused strip was gone, and Escape went nowhere until a click. When the page has
    /// nothing focusable, the navigation takes the focus so keys still arrive.
    /// </summary>
    private void FocusPage()
    {
        if (ContentFrame.Content is not FrameworkElement page) return;
        if (!page.IsLoaded)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                page.Loaded -= OnLoaded;
                FocusPage();
            }
            page.Loaded += OnLoaded;
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(ContentFrame.Content, page)) return;
            var focused = page is IPageInput { InitialFocus: { } target } ? target.Focus(FocusState.Programmatic)
                : FocusManager.FindFirstFocusableElement(page) is Control first && first.Focus(FocusState.Programmatic);
            if (!focused) Nav.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>Escape: back a level within the section, or close the app on the section's first page (no confirmation).</summary>
    private void Escape()
    {
        if (ContentFrame.BackStack.Count > (_sectionStarts.TryPeek(out var start) ? start : 0)) GoBack();
        else Close();
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    /// <summary>Back in the main frame (reader → gallery → search).</summary>
    public void GoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    /// <summary>Shows or hides the navigation pane (the reader hides it in full screen).</summary>
    public bool NavigationPaneVisible
    {
        get => Nav.IsPaneVisible;
        set => Nav.IsPaneVisible = value;
    }

    /// <summary>
    /// Back by Alt+Left and the mouse back button; Escape offered to the current page first.
    /// Handled while tunnelling (PreviewKeyDown at the root), so lists that consume arrow keys
    /// (the gallery's thumbnail grid) cannot swallow Alt+Left.
    /// </summary>
    private void InstallInput()
    {
        Nav.PreviewKeyDown += (_, e) =>
        {
            var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.F11)
            {
                FullScreen.Toggle();
                e.Handled = true;
            }
            else if (alt && e.Key == VirtualKey.Left)
            {
                GoBack();
                e.Handled = true;
            }
            // Escape belongs to an open dialog or flyout first (closing it must not also go back).
            else if (e.Key == VirtualKey.Escape
                     && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Count == 0)
            {
                if (ContentFrame.Content is not IPageInput page || !page.OnEscape()) Escape();
                e.Handled = true;
            }
        };
        // Going back past a section's first page (Alt+Left, Back button) returns to the section before it.
        ContentFrame.Navigated += (_, e) =>
        {
            if (e.NavigationMode != Microsoft.UI.Xaml.Navigation.NavigationMode.Back) return;
            while (_sectionStarts.Count > 1 && _sectionStarts.Peek() > ContentFrame.BackStack.Count) _sectionStarts.Pop();
        };
        // Current screen for UI Automation checks (pages have no automation peer of their own):
        // the page's AutomationId (SourceView, GalleryView, ReaderView, SettingsView) as the navigation's ItemStatus.
        ContentFrame.Navigated += (_, _) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(Nav,
            ContentFrame.Content is DependencyObject page ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(page) : "");
        ContentFrame.Navigated += (_, e) => SyncSelection(e.Parameter);
        // Keys reach the window only through a focused element; a new page starts with nothing focused.
        ContentFrame.Navigated += (_, _) => FocusPage();
        Nav.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(Nav).Properties.IsXButton1Pressed)
            {
                GoBack();
                e.Handled = true;
            }
        }), handledEventsToo: true);
    }
}
