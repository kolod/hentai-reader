using HentaiReader.Host.Downloads;
using HentaiReader.Host.Library;
using HentaiReader.Host.Storage;

namespace HentaiReader.App.Services;

/// <summary>App-wide services shared by the pages of the single main window.</summary>
public static class AppServices
{
    private static PageCache? _pageCache;
    private static readonly TaskCompletionSource<LibraryService> LibrarySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly List<Task> Pending = [];

    public static Preferences Preferences { get; private set; } = new();

    public static void Initialize(Preferences preferences) => Preferences = preferences;

    /// <summary>Disk cache of page images (FR-023), created on first use with the preference limit.</summary>
    public static PageCache PageCache => _pageCache ??= new PageCache(Preferences.CacheFolder ?? AppPaths.PageCache,
        Preferences.CacheLimitMb * 1024L * 1024L, moveFrom: Preferences.CacheMoveFrom);

    /// <summary>Progress of a cache move (moved, total) or its failure message; raised on worker threads.</summary>
    public static event Action<(int Moved, int Total)?, string?>? CacheMoveChanged;

    private static Task? _cacheMove;

    /// <summary>Moves the page cache to <paramref name="folder"/> in the background (FR-024); the same folder as before cancels a running move.</summary>
    public static void MoveCache(string folder)
    {
        var cache = PageCache;
        cache.BeginMove(folder);
        Preferences.CacheFolder = cache.Folder;
        Preferences.CacheMoveFrom = cache.MovingFrom;
        Preferences.Save();
        ContinueCacheMove();
    }

    /// <summary>Continues an unfinished move (also after a restart). Failures keep both folders readable (FR-025).</summary>
    public static void ContinueCacheMove()
    {
        var cache = PageCache;
        if (cache.MovingFrom is null || _cacheMove is { IsCompleted: false }) return;
        _cacheMove = Task.Run(async () =>
        {
            try
            {
                while (await cache.MoveStepAsync(20))
                {
                    var (remaining, total) = cache.MoveProgress;
                    CacheMoveChanged?.Invoke((total - remaining, total), null);
                }
                Preferences.CacheMoveFrom = null;
                Preferences.Save();
                CacheMoveChanged?.Invoke(null, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                CacheMoveChanged?.Invoke(null, e.Message);
            }
        });
    }

    /// <summary>The library database (006); completes once opened after the adult confirmation (FR-028).</summary>
    public static Task<LibraryService> Library => LibrarySource.Task;

    /// <summary>Opens <c>library.db</c> (and imports the 005 progress file once). Called after the adult confirmation.</summary>
    public static async Task OpenLibraryAsync()
    {
        if (LibrarySource.Task.IsCompleted) return;
        var library = await Task.Run(() => LibraryService.OpenAsync(AppPaths.Database, AppPaths.Progress, AppPaths.DefaultLibrary));
        LibrarySource.TrySetResult(library);
    }

    /// <summary>Save-to-library queue (US1, US5); null until the library and sources are loaded.</summary>
    public static DownloadQueue? Downloads { get; private set; }

    /// <summary>Starts the download queue once sources are loaded; unfinished downloads continue (FR-005).</summary>
    public static async Task StartDownloadsAsync(Func<string, DownloadSource?> sources)
    {
        if (Downloads is not null) return;
        var queue = new DownloadQueue(await Library, sources, PageCache);
        SaveTracker.Attach(queue);
        Downloads = queue;
        await Task.Run(() => queue.StartAsync());
    }

    /// <summary>Folder scanning and watching (US3); null until the library is open.</summary>
    public static LibraryWatcher? Watcher { get; private set; }

    /// <summary>Scans the library folders and watches them; changes refresh the library screens.</summary>
    public static async Task StartWatcherAsync()
    {
        if (Watcher is not null) return;
        var watcher = new LibraryWatcher(await Library);
        watcher.Changed += () => SaveTracker.RaiseChanged(default);
        Watcher = watcher;
        await Task.Run(() => watcher.StartAsync());
    }

    /// <summary>Remembers a background write so closing the app waits for it.</summary>
    public static void Track(Task task)
    {
        lock (Pending)
        {
            Pending.RemoveAll(t => t.IsCompleted);
            Pending.Add(task);
        }
    }

    /// <summary>
    /// When the window closes: waits (briefly) for pending writes, writes reading state into saved
    /// archives, flushes the cache bookkeeping.
    /// </summary>
    public static void Shutdown()
    {
        Task[] pending;
        lock (Pending) pending = [.. Pending];
        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(5));
            if (LibrarySource.Task.IsCompletedSuccessfully)
                Task.Run(() => LibrarySource.Task.Result.FlushMetadataAsync()).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // A failed write must not keep the app from closing; dirty archives are retried next start.
        }
        Downloads?.Dispose();
        Watcher?.Dispose();
        _pageCache?.Dispose();
    }
}
