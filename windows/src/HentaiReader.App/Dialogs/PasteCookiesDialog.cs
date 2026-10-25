using System.Text.Json.Nodes;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Reading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HentaiReader.App.Dialogs;

/// <summary>
/// Sign-in by pasting cookies (FR-028): <c>name=value; …</c> or a cookies.txt file. Only cookies of the
/// source's domains are kept (sign-in cookies also for its shareWith domains); the values are never
/// shown again.
/// </summary>
public static class PasteCookiesDialog
{
    /// <summary>True when cookies were stored (the caller verifies them with getAccountStatus).</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, SourceEntry source)
    {
        var manifest = source.Session!.Manifest;
        var domains = (manifest["domains"] as JsonArray ?? []).Select(d => d!.GetValue<string>()).ToList();
        var loginUrl = (manifest["loginPage"] as JsonObject)?["url"]?.GetValue<string>();
        var defaultDomain = loginUrl is not null ? new Uri(loginUrl).Host : domains.First(d => !d.StartsWith("*.", StringComparison.Ordinal));
        var visibleDomains = string.Join(", ", domains.Where(d => !d.StartsWith("*.", StringComparison.Ordinal)));

        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, IsSpellCheckEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, "PasteCookiesBox");
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Strings.Get("Paste_Title"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = Strings.Format("Paste_Hint", visibleDomains), TextWrapping = TextWrapping.Wrap }, box },
            },
            PrimaryButtonText = Strings.Get("Paste_Save"),
            CloseButtonText = Strings.Get("Reader_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        var cookies = LoginCookies.Select(manifest, LoginCookies.Parse(box.Text, defaultDomain));
        box.Text = "";
        if (cookies.Count == 0) return false;
        source.Cookies!.Add(cookies);
        return true;
    }
}
