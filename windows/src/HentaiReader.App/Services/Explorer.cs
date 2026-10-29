using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HentaiReader.App.Services;

/// <summary>
/// Shows folders and files in File Explorer. A window that already shows the folder is brought to the
/// front (and the file selected in it) instead of opening another window each time. The open windows
/// come from the Shell.Application automation object; when it fails, a new window is opened as before.
/// </summary>
public static class Explorer
{
    private const int SwRestore = 9;
    // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
    private const int SelectOnly = 0x1 | 0x4 | 0x8 | 0x10;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        object? window = Find(folder);
        if (window is not null && Focus(window)) return;
        Process.Start("explorer.exe", $"\"{folder}\"");
    }

    /// <summary>The file selected in its folder's window.</summary>
    public static void ShowFile(string file)
    {
        if (!File.Exists(file)) return;
        object? window = Path.GetDirectoryName(file) is { } folder ? Find(folder) : null;
        if (window is not null && Select(window, file) && Focus(window)) return;
        Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    /// <summary>The Explorer window (or tab) showing <paramref name="folder"/>, or null.</summary>
    private static object? Find(string folder)
    {
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    // Not every shell window is a folder view (Document may be anything, or nothing).
                    string? path = window.Document?.Folder?.Self?.Path;
                    if (path is not null && SamePath(path, folder)) return (object)window;
                }
                catch (Exception e) when (IsAutomationFailure(e))
                {
                }
            }
        }
        catch (Exception e) when (IsAutomationFailure(e))
        {
        }
        return null;
    }

    private static bool Select(object window, string file)
    {
        try
        {
            dynamic document = ((dynamic)window).Document;
            object? item = document.Folder.ParseName(Path.GetFileName(file));
            if (item is null) return false;
            document.SelectItem(item, SelectOnly);
            return true;
        }
        catch (Exception e) when (IsAutomationFailure(e))
        {
            return false;
        }
    }

    private static bool Focus(object window)
    {
        try
        {
            // Windows 11 tabs report their own handle; the frame is what comes to the front.
            var hwnd = (nint)Convert.ToInt64(((dynamic)window).HWND);
            if (GetAncestor(hwnd, GaRoot) is var root and not 0) hwnd = root;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            return SetForegroundWindow(hwnd);
        }
        catch (Exception e) when (IsAutomationFailure(e))
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;   // virtual folders ("::{GUID}")
        }
    }

    private static bool IsAutomationFailure(Exception e) =>
        e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidComObjectException;
}
