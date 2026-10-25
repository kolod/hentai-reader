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
            RememberPlacement();
            _preferences.Save();
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

        await _sources.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Sources"), _preferences);
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

        var start = _sources.Find(_startSourceId)?.IsAvailable == true ? _startSourceId
            : _sources.Find(_preferences.LastSourceId)?.IsAvailable == true ? _preferences.LastSourceId
            : _sources.Entries.FirstOrDefault(e => e.IsAvailable)?.Id;
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == start);
    }

    /// <summary>
    /// Marks the navigation item of the page now shown, also after Back. Otherwise the item left
    /// selected (Settings after going back from it) could not be chosen again: clicking an
    /// already selected item raises no SelectionChanged.
    /// </summary>
    private void SyncSelection(object? parameter)
    {
        var sourceId = parameter switch
        {
            SourcePage.Args a => a.Source.Id,
            GalleryPage.Args a => a.Source.Id,
            ReaderPage.Args a => a.Source.Id,
            _ => null,
        };
        object? item = ContentFrame.Content is SettingsPage ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == sourceId);
        if (item is null || ReferenceEquals(Nav.SelectedItem, item)) return;
        _syncingSelection = true;
        Nav.SelectedItem = item;
        _syncingSelection = false;
    }

    /// <summary>Puts the window bounds into the preferences (saved by the caller).</summary>
    public void RememberPlacement() => _preferences.Window = _placement.Current;

    private Task WaitForContentAsync()
    {
        if (Content.XamlRoot is not null) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        ((FrameworkElement)Content).Loaded += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage), new SettingsPage.Args(_preferences, _preferences.LastSourceId, _sources.Entries));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: string id } && _sources.Find(id) is { IsAvailable: true } entry)
        {
            _preferences.LastSourceId = id;
            _preferences.Save();
            ContentFrame.Navigate(typeof(SourcePage), new SourcePage.Args(entry, null));
        }
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
            else if (e.Key == VirtualKey.Escape && ContentFrame.Content is IPageInput page
                     && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Count == 0)
            {
                e.Handled = page.OnEscape();
            }
        };
        // Current screen for UI Automation checks (pages have no automation peer of their own):
        // the page's AutomationId (SourceView, GalleryView, ReaderView, SettingsView) as the navigation's ItemStatus.
        ContentFrame.Navigated += (_, _) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(Nav,
            ContentFrame.Content is DependencyObject page ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(page) : "");
        ContentFrame.Navigated += (_, e) => SyncSelection(e.Parameter);
        // Keys reach the window only through a focused element; a new page starts with nothing focused.
        ContentFrame.Navigated += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (ContentFrame.Content is IPageInput { InitialFocus: { } target }) target.Focus(FocusState.Programmatic);
            else if (ContentFrame.Content is DependencyObject page && FocusManager.FindFirstFocusableElement(page) is Control first)
                first.Focus(FocusState.Programmatic);
        });
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
