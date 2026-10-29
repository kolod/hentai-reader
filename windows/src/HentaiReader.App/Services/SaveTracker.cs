using System.Text.Json.Nodes;
using HentaiReader.App.Localization;
using HentaiReader.Host.Data;
using HentaiReader.Host.Downloads;
using HentaiReader.Host.Engine;

namespace HentaiReader.App.Services;

/// <summary>Save-to-library state of one gallery as the gallery screen and the reader show it (library-ui.md).</summary>
public sealed record SaveInfo(DownloadRow? Download, LibraryItem? Saved)
{
    public bool IsSaved => Saved is not null && Download is null;
    public bool CanCancel => Download is { State: not DownloadState.Done };

    /// <summary>Button text: Save to library / Saving 12 / 40 / Paused … / Stopped … / In library.</summary>
    public string Text => Download switch
    {
        null when Saved is not null => Strings.Get("Save_InLibrary"),
        null => Strings.Get("Save_ToLibrary"),
        { State: DownloadState.Queued } => Strings.Get("Save_Queued"),
        { State: DownloadState.Downloading or DownloadState.Assembling } d => Strings.Format("Save_Saving", d.PagesDone, d.PageCount),
        { State: DownloadState.Paused } d => Strings.Format("Save_Paused", d.PagesDone, d.PageCount),
        { State: DownloadState.Limited, ErrorKind: SourceErrorKind.LoginRequired } => Strings.Get("Save_LoginRequired"),
        { State: DownloadState.Limited } => Strings.Get("Save_Limited"),
        { State: DownloadState.Failed } => Strings.Get("Save_Failed"),
        _ => Strings.Get("Save_InLibrary"),
    };

    /// <summary>Tooltip: what a click does, plus the reason of a stop.</summary>
    public string ToolTip => Download switch
    {
        { State: DownloadState.Queued or DownloadState.Downloading or DownloadState.Assembling } => Strings.Get("Save_ClickToPause"),
        { State: DownloadState.Limited or DownloadState.Failed } d => d.ErrorMessage ?? Text,
        _ => Text,
    };
}

/// <summary>Reads and changes the save state of galleries; <see cref="Changed"/> fires on worker threads.</summary>
public static class SaveTracker
{
    /// <summary>A download changed (its gallery key).</summary>
    public static event Action<GalleryKey>? Changed;

    public static void Attach(DownloadQueue queue) => queue.Changed += row => Changed?.Invoke(row.Key);

    /// <summary>Raised after a library change outside the queue (deletion, scan).</summary>
    public static void RaiseChanged(GalleryKey key) => Changed?.Invoke(key);

    public static async Task<SaveInfo> GetAsync(GalleryKey key)
    {
        var library = await AppServices.Library;
        var download = await library.Downloads.ActiveForAsync(key);
        return new SaveInfo(download, await library.FindSavedAsync(key));
    }

    /// <summary>
    /// Click on the save button: queue, pause, resume or report that the gallery is saved (the caller
    /// then opens the saved copy). Returns the saved copy to open, if any.
    /// </summary>
    public static async Task<LibraryItem?> ClickAsync(SaveInfo info, SourceEntry? source, JsonObject? gallery)
    {
        var queue = AppServices.Downloads;
        if (queue is null) return null;
        switch (info.Download)
        {
            case null when info.Saved is not null:
                return info.Saved;
            case null when source is not null && gallery is not null:
                await queue.EnqueueAsync(source.Id, gallery, source.Settings?.Values);
                return null;
            case { State: DownloadState.Queued or DownloadState.Downloading or DownloadState.Assembling } d:
                await queue.PauseAsync(d.Id);
                return null;
            case { } d:
                await queue.ResumeAsync(d.Id);
                return null;
            default:
                return null;
        }
    }

    public static async Task CancelAsync(SaveInfo info)
    {
        if (info.Download is { } d && AppServices.Downloads is { } queue) await queue.CancelAsync(d.Id);
    }
}

