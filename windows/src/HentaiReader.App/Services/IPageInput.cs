namespace HentaiReader.App.Services;

/// <summary>
/// Window-level keys a page may claim (specs/005-windows-reader contracts/reader-ui.md): the main
/// window asks the current page first and falls back to its own behaviour.
/// </summary>
public interface IPageInput
{
    /// <summary>Escape: true when the page used it (e.g. the reader leaving full screen or going back).</summary>
    bool OnEscape();

    /// <summary>Element to focus after navigation (default: the first focusable one).</summary>
    Microsoft.UI.Xaml.Controls.Control? InitialFocus => null;
}
