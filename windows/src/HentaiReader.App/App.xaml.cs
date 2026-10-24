using System.Globalization;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using Microsoft.UI.Xaml;

namespace HentaiReader.App;

public partial class App : Application
{
    /// <summary>
    /// Held while the app runs so the installer can ask to close it first (Inno Setup
    /// <c>AppMutex</c>, feature 004). The global name covers other sessions of an all-users
    /// installation. Several instances may run; this is not a single-instance lock.
    /// </summary>
    public const string RunningMutexName = "HentaiReader.Running";

    // Referenced for the process lifetime; never disposed before exit.
    internal readonly Mutex?[] RunningMutexes = [TryCreateMutex(RunningMutexName), TryCreateMutex(@"Global\" + RunningMutexName)];
    private MainWindow? _window;
    private readonly Preferences _preferences = Preferences.Load();

    private static Mutex? TryCreateMutex(string name)
    {
        try
        {
            return new Mutex(false, name);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    public App()
    {
        // The language override must be in place before any resource loads (research R6).
        var language = _preferences.EffectiveLanguage();
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        Strings.Language = language;
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo(language);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var startSource = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--source=", StringComparison.Ordinal))?["--source=".Length..];
        _window = new MainWindow(_preferences, startSource);
        _window.Activate();
        _window.StartAsync();
    }
}
