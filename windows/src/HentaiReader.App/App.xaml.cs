using System.Globalization;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using Microsoft.UI.Xaml;

namespace HentaiReader.App;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly Preferences _preferences = Preferences.Load();

    public App()
    {
        // The language override must be in place before any resource loads (research R6).
        AppServices.Initialize(_preferences);
        var language = _preferences.EffectiveLanguage();
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        Strings.Language = language;
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo(language);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        // A second launch shows the running window instead of opening another one.
        if (!SingleInstance.TryAcquire(restarting: commandLine.Contains(SingleInstance.RestartFlag)))
        {
            SingleInstance.ActivateOther();
            Exit();
            return;
        }

        // Sign-in browser profiles left behind by a crash (they are deleted after each sign-in).
        Dialogs.LoginDialog.CleanUp();

        var startSource = commandLine
            .FirstOrDefault(a => a.StartsWith("--source=", StringComparison.Ordinal))?["--source=".Length..];
        _window = new MainWindow(_preferences, startSource);
        _window.Activate();
        _window.ApplyPlacement();
        _window.StartAsync();
    }
}
