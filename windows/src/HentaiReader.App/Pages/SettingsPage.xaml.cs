using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;

namespace HentaiReader.App.Pages;

/// <summary>
/// Interface language, independent of the system language (US4). The platform applies a language
/// override only before resources load, so a change restarts the app on the same source.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public sealed record Args(Preferences Preferences, string? CurrentSourceId);

    private sealed record Option(string Tag, string Text);

    private Args? _args;
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        TitleText.Text = Strings.Get("Settings_Title");
        LanguageBox.Header = Strings.Get("Settings_Language");
        RestartNote.Text = Strings.Get("Settings_LanguageRestart");
        LanguageBox.SelectionChanged += LanguageBox_SelectionChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Args args) return;
        _args = args;
        var options = new List<Option> { new("", Strings.Get("Settings_LanguageSystem")) };
        options.AddRange(Preferences.Languages.Select(tag => new Option(tag, Strings.LanguageName(tag))));
        _ready = false;
        LanguageBox.ItemsSource = options;
        LanguageBox.SelectedItem = options.FirstOrDefault(o => o.Tag == args.Preferences.Language) ?? options[0];
        _ready = true;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _args is null || LanguageBox.SelectedItem is not Option option || option.Tag == _args.Preferences.Language) return;
        _args.Preferences.Language = option.Tag;
        _args.Preferences.Save();
        // The new process waits for this one to release the single-instance mutex (--restart).
        SingleInstance.Release();
        var arguments = SingleInstance.RestartFlag + (_args.CurrentSourceId is null ? "" : $" --source={_args.CurrentSourceId}");
        AppInstance.Restart(arguments);
    }
}
