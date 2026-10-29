namespace HentaiReader.App.Services;

/// <summary>
/// Window-level keys a page may claim (specs/005-windows-reader contracts/reader-ui.md): the main
/// window asks the current page first and falls back to its own behaviour.
/// </summary>
public interface IPageInput
{
    /// <summary>
    /// Escape: true when the page used it for something of its own. Otherwise the window goes back a
    /// level, or closes on the first page of a section. Full screen is F11's alone.
    /// </summary>
    bool OnEscape() => false;

    /// <summary>Element to focus after navigation (default: the first focusable one).</summary>
    Microsoft.UI.Xaml.Controls.Control? InitialFocus => null;
}
