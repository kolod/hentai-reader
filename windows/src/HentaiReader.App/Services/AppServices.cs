using HentaiReader.Host.Storage;

namespace HentaiReader.App.Services;

/// <summary>App-wide services shared by the pages of the single main window.</summary>
public static class AppServices
{
    private static PageCache? _pageCache;
    private static ProgressStore? _progress;

    public static Preferences Preferences { get; private set; } = new();

    public static void Initialize(Preferences preferences) => Preferences = preferences;

    /// <summary>Disk cache of page images (FR-023), created on first use with the preference limit.</summary>
    public static PageCache PageCache => _pageCache ??= new PageCache(AppPaths.PageCache, Preferences.CacheLimitMb * 1024L * 1024L);

    /// <summary>Reading progress per source and gallery (FR-018).</summary>
    public static ProgressStore Progress => _progress ??= new ProgressStore(AppPaths.Progress);

    /// <summary>Writes pending cache bookkeeping; called when the window closes.</summary>
    public static void Shutdown() => _pageCache?.Dispose();
}
