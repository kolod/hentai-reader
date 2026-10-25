using System.Text.Json.Nodes;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Http;
using HentaiReader.Host.Reading;
using HentaiReader.Host.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace HentaiReader.App.Dialogs;

/// <summary>
/// Sign-in on the source's own login page (contract 1.2 <c>manifest.loginPage</c>, FR-028/FR-029).
/// The embedded browser uses a throw-away profile that is deleted afterwards, may only navigate within
/// the source's domains (other links open in the system browser), and finishes as soon as every
/// sign-in cookie is present; then only the source's cookies go into its encrypted jar.
/// </summary>
public static class LoginDialog
{
    /// <summary>True when the sign-in completed and the cookies were stored.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, SourceEntry source)
    {
        var manifest = source.Session!.Manifest;
        if (manifest["loginPage"] is not JsonObject loginPage) return false;
        var domains = (manifest["domains"] as JsonArray ?? []).Select(d => d!.GetValue<string>()).ToList();
        var loginUrl = loginPage["url"]!.GetValue<string>();

        // Fits low windows too: the dialog's title, hint and buttons need about 240 px.
        var view = new WebView2 { Width = Math.Min(880, root.Size.Width - 120), Height = Math.Clamp(root.Size.Height - 240, 240, 600) };
        var status = new TextBlock { Text = Strings.Get("Login_Hint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Strings.Format("Login_Title", source.Name),
            Content = new StackPanel { Spacing = 8, Children = { status, view } },
            CloseButtonText = Strings.Get("Reader_Cancel"),
        };
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;
        dialog.Resources["ContentDialogMaxHeight"] = 820.0;

        var profile = Path.Combine(AppPaths.WebView, Guid.NewGuid().ToString("N"));
        var signedIn = false;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, new CoreWebView2EnvironmentOptions());
                await view.EnsureCoreWebView2Async(environment);
            }
            catch (Exception)
            {
                // No WebView2 runtime on this computer: pasting cookies remains (spec assumption).
                status.Text = Strings.Get("Login_NoWebView");
                view.Visibility = Visibility.Collapsed;
                return;
            }
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += (sender, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && ManifestRules.HostInDomains(uri.Host, domains)) return;
                e.Cancel = true;
                if (uri is not null) _ = Windows.System.Launcher.LaunchUriAsync(uri);
            };
            core.NewWindowRequested += (sender, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
                {
                    if (ManifestRules.HostInDomains(uri.Host, domains)) core.Navigate(e.Uri);
                    else _ = Windows.System.Launcher.LaunchUriAsync(uri);
                }
            };
            core.NavigationCompleted += async (_, _) =>
            {
                var cookies = (await core.CookieManager.GetCookiesAsync(loginUrl)).Select(Convert).ToList();
                if (signedIn || LoginCookies.Missing(loginPage, cookies).Count > 0) return;
                signedIn = true;
                source.Cookies!.Add(LoginCookies.Select(manifest, cookies));
                dialog.Hide();
            };
            core.Navigate(loginUrl);
        };

        await dialog.ShowAsync();
        view.Close();
        DeleteLater(profile);
        return signedIn;
    }

    private static CookieJar.Cookie Convert(CoreWebView2Cookie c) =>
        new(c.Name, c.Value, c.Domain, !c.Domain.StartsWith('.'), string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
            c.IsSession ? null : (long)(c.Expires * 1000));

    /// <summary>The browser processes release the profile shortly after closing; retry for a while.</summary>
    private static void DeleteLater(string profile) => _ = Task.Run(async () =>
    {
        for (var i = 0; i < 20 && Directory.Exists(profile); i++)
        {
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
    });

    /// <summary>Leftover profiles of a crashed session are removed at start.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(AppPaths.WebView)) Directory.Delete(AppPaths.WebView, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by another browser process; tried again next start.
        }
    }
}
