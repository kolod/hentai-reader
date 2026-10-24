using HentaiReader.App.Localization;
using HentaiReader.App.Pages;
using HentaiReader.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HentaiReader.App;

/// <summary>
/// One window: the bundled sources as navigation items, settings in the footer. Sources are only
/// loaded after the adult-content confirmation, so no source can make a request before it (FR-021).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Preferences _preferences;
    private readonly string? _startSourceId;
    private readonly SourceRegistry _sources = new();

    public MainWindow(Preferences preferences, string? startSourceId)
    {
        InitializeComponent();
        _preferences = preferences;
        _startSourceId = startSourceId;
        Title = Strings.Get("App_Title");
        Nav.PaneTitle = Strings.Get("Nav_Sources");
        Closed += (_, _) => _sources.Dispose();
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

        await _sources.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Sources"));
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

    private Task WaitForContentAsync()
    {
        if (Content.XamlRoot is not null) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        ((FrameworkElement)Content).Loaded += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage), new SettingsPage.Args(_preferences, _preferences.LastSourceId));
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: string id } && _sources.Find(id) is { IsAvailable: true } entry)
        {
            _preferences.LastSourceId = id;
            _preferences.Save();
            ContentFrame.Navigate(typeof(SourcePage), new SourcePage.Args(entry, null));
        }
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }
}
