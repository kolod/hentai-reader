using System.Text.Json.Nodes;
using HentaiReader.App.Dialogs;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;

namespace HentaiReader.App.Pages;

/// <summary>
/// Settings: interface language (a change restarts the app on the same source), reader options
/// (FR-033), and per source its declared settings and account (FR-026–FR-032).
/// </summary>
public sealed partial class SettingsPage : Page
{
    public sealed record Args(Preferences Preferences, string? CurrentSourceId, IReadOnlyList<SourceEntry> Sources);

    private sealed record Option(string Tag, string Text);

    private static readonly int[] CacheLimitsMb = [256, 512, 1024, 2048, 5120, 10240];

    private Args? _args;
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        TitleText.Text = Strings.Get("Settings_Title");
        LanguageBox.Header = Strings.Get("Settings_Language");
        RestartNote.Text = Strings.Get("Settings_LanguageRestart");
        ReaderHeader.Text = Strings.Get("Settings_Reader");
        PreloadBox.Header = Strings.Get("Settings_Preload");
        FitWidthSwitch.Header = Strings.Get("Settings_FitWidth");
        // The default On/Off follow the system language, not the app's.
        FitWidthSwitch.OnContent = Strings.Get("Settings_On");
        FitWidthSwitch.OffContent = Strings.Get("Settings_Off");
        GapHint.Text = Strings.Get("Settings_PageGapHint");
        CacheLimitBox.Header = Strings.Get("Settings_CacheLimit");
        ClearCacheButton.Content = Strings.Get("Settings_ClearCache");
        SourcesHeader.Text = Strings.Get("Settings_Sources");
        LanguageBox.SelectionChanged += LanguageBox_SelectionChanged;
        PreloadBox.SelectionChanged += (_, _) => SaveReaderSettings();
        CacheLimitBox.SelectionChanged += (_, _) => SaveReaderSettings();
        FitWidthSwitch.Toggled += (_, _) => SaveReaderSettings();
        GapSlider.ValueChanged += (_, _) =>
        {
            ShowGap();
            SaveReaderSettings();
        };
        WidthSlider.ValueChanged += (_, _) =>
        {
            WidthLabel.Text = $"{Strings.Get("Settings_ReadingWidth")}: {Strings.Format("Settings_ReadingWidthValue", (int)WidthSlider.Value)}";
            SaveReaderSettings();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Args args) return;
        _args = args;
        _ready = false;

        var options = new List<Option> { new("", Strings.Get("Settings_LanguageSystem")) };
        options.AddRange(Preferences.Languages.Select(tag => new Option(tag, Strings.LanguageName(tag))));
        LanguageBox.ItemsSource = options;
        LanguageBox.SelectedItem = options.FirstOrDefault(o => o.Tag == args.Preferences.Language) ?? options[0];

        PreloadBox.ItemsSource = new[] { 3, 4, 5 };
        PreloadBox.SelectedItem = Math.Clamp(args.Preferences.PreloadPages, 3, 5);
        WidthSlider.Value = Math.Clamp(args.Preferences.ReadingWidth, 600, 2000);
        WidthLabel.Text = $"{Strings.Get("Settings_ReadingWidth")}: {Strings.Format("Settings_ReadingWidthValue", (int)WidthSlider.Value)}";
        FitWidthSwitch.IsOn = args.Preferences.FitWidth;
        GapSlider.Value = Math.Clamp(args.Preferences.PageGap, 0, 48);
        ShowGap();
        var limits = CacheLimitsMb.Select(mb => new Option(mb.ToString(), Size(mb * 1024L * 1024L))).ToList();
        CacheLimitBox.ItemsSource = limits;
        CacheLimitBox.SelectedItem = limits.FirstOrDefault(o => o.Tag == args.Preferences.CacheLimitMb.ToString()) ?? limits[2];
        ShowCacheSize();

        SourcesPanel.Children.Clear();
        foreach (var source in args.Sources.Where(s => s.IsAvailable)) SourcesPanel.Children.Add(BuildSource(source));
        _ready = true;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _args is null || LanguageBox.SelectedItem is not Option option || option.Tag == _args.Preferences.Language) return;
        _args.Preferences.Language = option.Tag;
        MainWindow.Instance?.RememberPlacement();
        _args.Preferences.Save();
        // The new process waits for this one to release the single-instance mutex (--restart).
        SingleInstance.Release();
        var arguments = SingleInstance.RestartFlag + (_args.CurrentSourceId is null ? "" : $" --source={_args.CurrentSourceId}");
        AppInstance.Restart(arguments);
    }

    // --- reader -----------------------------------------------------------------------------------

    private void ShowGap() => GapLabel.Text = $"{Strings.Get("Settings_PageGap")}: " +
        (GapSlider.Value > 0 ? Strings.Format("Settings_ReadingWidthValue", (int)GapSlider.Value) : Strings.Get("Settings_PageGapOff"));

    private void SaveReaderSettings()
    {
        if (!_ready || _args is null) return;
        var preferences = _args.Preferences;
        if (PreloadBox.SelectedItem is int preload) preferences.PreloadPages = preload;
        preferences.ReadingWidth = (int)WidthSlider.Value;
        preferences.FitWidth = FitWidthSwitch.IsOn;
        preferences.PageGap = (int)GapSlider.Value;
        if (CacheLimitBox.SelectedItem is Option limit)
        {
            preferences.CacheLimitMb = int.Parse(limit.Tag);
            AppServices.PageCache.LimitBytes = preferences.CacheLimitMb * 1024L * 1024L;
            ShowCacheSize();
        }
        preferences.Save();
    }

    private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        AppServices.PageCache.Clear();
        ShowCacheSize();
    }

    private void ShowCacheSize() => CacheSizeText.Text = Strings.Format("Settings_CacheSize", Size(AppServices.PageCache.SizeBytes));

    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? Strings.Format("Size_GB", Strings.Number(bytes / (1024.0 * 1024 * 1024), "0.#"))
        : Strings.Format("Size_MB", Strings.Number(bytes / (1024.0 * 1024), "0"));

    // --- sources ----------------------------------------------------------------------------------

    private FrameworkElement BuildSource(SourceEntry source)
    {
        var panel = new StackPanel { Spacing = 10 };
        var message = new InfoBar { IsClosable = true, Severity = InfoBarSeverity.Warning };
        panel.Children.Add(message);
        var expander = new Expander
        {
            Header = source.Name,
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(expander, "SourceSettings_" + source.Id);

        foreach (var (id, values, _) in source.Settings?.Declared ?? [])
        {
            var items = values.Select(v => new Option(v, Strings.SettingValueLabel(id, v))).ToList();
            var box = new ComboBox { Header = Strings.SettingLabel(id), ItemsSource = items, DisplayMemberPath = "Text", MinWidth = 260 };
            box.SelectedItem = items.FirstOrDefault(o => o.Tag == source.Settings!.ValueOf(id));
            var previous = box.SelectedItem as Option;
            box.SelectionChanged += async (_, _) =>
            {
                if (box.SelectedItem is not Option chosen || chosen == previous) return;
                // ExHentai only with an account that has access (FR-031); nothing is sent that would fail.
                if (id == "site" && chosen.Tag == "exhentai" && !await HasExHentaiAccessAsync(source))
                {
                    box.SelectedItem = previous;
                    Show(message, Strings.Get("ExHentai_NoAccess"));
                    return;
                }
                source.Settings!.Set(id, chosen.Tag);
                previous = chosen;
            };
            panel.Children.Add(box);
        }

        if (source.Session!.Manifest["capabilities"]?["accountStatus"]?.GetValue<bool>() == true)
        {
            panel.Children.Add(BuildAccount(source, message));
        }
        return expander;
    }

    private FrameworkElement BuildAccount(SourceEntry source, InfoBar message)
    {
        var state = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var limit = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var signIn = new Button { Content = Strings.Get("Account_SignIn") };
        var paste = new Button { Content = Strings.Get("Account_PasteCookies") };
        var signOut = new Button { Content = Strings.Get("Account_SignOut") };
        var refresh = new Button { Content = Strings.Get("Account_Refresh") };
        if (source.Session!.Manifest["loginPage"] is JsonObject) buttons.Children.Add(signIn);
        buttons.Children.Add(paste);
        buttons.Children.Add(signOut);
        buttons.Children.Add(refresh);

        async Task RefreshAsync()
        {
            state.Text = Strings.Get("Account_Checking");
            limit.Text = "";
            var status = await AccountStatusAsync(source);
            Render(status);
        }

        void Render(JsonObject? status)
        {
            var signedIn = status?["loggedIn"]?.GetValue<bool>() == true;
            state.Text = Strings.Get(status is null ? "Account_Checking" : signedIn ? "Account_SignedIn" : "Account_SignedOut");
            limit.Text = "";
            if (status?["imageLimit"] is JsonObject l)
            {
                limit.Text = Strings.Format("Account_ImageLimit", Strings.Number(l["used"]!.GetValue<double>(), "N0"), Strings.Number(l["max"]!.GetValue<double>(), "N0"));
                if (l["resetsAt"] is JsonValue r && DateTimeOffset.TryParse(r.GetValue<string>(), out var at))
                    limit.Text += " · " + Strings.Format("Account_LimitResets", Strings.Date(at));
            }
            signOut.IsEnabled = signedIn || source.Cookies?.NamesFor(new Uri(LoginUrl(source)).Host).Count > 0;
        }

        signIn.Click += async (_, _) =>
        {
            if (await LoginDialog.ShowAsync(XamlRoot, source)) await RefreshAsync();
        };
        paste.Click += async (_, _) =>
        {
            if (!await PasteCookiesDialog.ShowAsync(XamlRoot, source)) return;
            await RefreshAsync();
            if (source.LastAccountStatus?["loggedIn"]?.GetValue<bool>() != true) Show(message, Strings.Get("Paste_Rejected"));
        };
        signOut.Click += (_, _) =>
        {
            source.SignOut();
            Render(new JsonObject { ["loggedIn"] = false });
        };
        refresh.Click += async (_, _) => await RefreshAsync();

        Render(source.LastAccountStatus);
        _ = RefreshAsync();
        return new StackPanel { Spacing = 6, Children = { state, limit, buttons } };
    }

    private static string LoginUrl(SourceEntry source) =>
        (source.Session!.Manifest["loginPage"] as JsonObject)?["url"]?.GetValue<string>()
        ?? "https://" + (source.Session.Manifest["domains"] as JsonArray)!.Select(d => d!.GetValue<string>()).First(d => !d.StartsWith('*'));

    /// <summary>getAccountStatus with the stored settings; remembered for the reader (FR-027). Null when unavailable.</summary>
    private static async Task<JsonObject?> AccountStatusAsync(SourceEntry source)
    {
        if (source.Session!.State == SessionState.Limited) return source.LastAccountStatus;
        var result = await source.CallAsync("getAccountStatus", []);
        if (!result.Ok || result.Value is not JsonObject status) return null;
        source.LastAccountStatus = status;
        return status;
    }

    private static async Task<bool> HasExHentaiAccessAsync(SourceEntry source) =>
        (await AccountStatusAsync(source))?["exhentai"]?.GetValue<bool>() == true;

    private static void Show(InfoBar bar, string text)
    {
        bar.Message = text;
        bar.IsOpen = true;
    }
}
