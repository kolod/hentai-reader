using HentaiReader.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HentaiReader.App.Dialogs;

/// <summary>One-time adult-content confirmation before any source is loaded (US5).</summary>
public static class AdultGateDialog
{
    public static async Task<bool> ConfirmAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Strings.Get("Gate_Title"),
            Content = new TextBlock { Text = Strings.Get("Gate_Body"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Strings.Get("Gate_Confirm"),
            CloseButtonText = Strings.Get("Gate_Decline"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
