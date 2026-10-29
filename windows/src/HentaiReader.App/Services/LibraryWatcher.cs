using HentaiReader.Host.Data;
using HentaiReader.Host.Library;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HentaiReader.App.Services;

/// <summary>
/// Keeps the library in step with its folders (US3, SC-008): a full scan at start, then one file
/// watcher per folder whose changes trigger a rescan of that folder after 1 s of quiet; folders that
/// are missing (disk unplugged) are checked every 30 s and rescanned when they come back.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    private readonly LibraryService _library;
    private readonly LibraryScanner _scanner;
    private readonly Dictionary<long, (FileSystemWatcher Watcher, Timer Debounce)> _watched = new();
    private readonly Dictionary<long, bool> _available = new();
    private readonly Timer _availability;
    private readonly object _lock = new();
    private bool _disposed;

    public LibraryWatcher(LibraryService library)
    {
        _library = library;
        _scanner = new LibraryScanner(library) { Measure = MeasureWithDecoder };
        _availability = new Timer(_ => _ = CheckAvailabilityAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public LibraryScanner Scanner => _scanner;

    /// <summary>Raised after a scan changed something (from a worker thread).</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        await RefreshAsync();
        _availability.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Scans every folder and (re)creates the watchers; call after folders were added or removed.</summary>
    public async Task RefreshAsync()
    {
        var folders = await _library.Folders.ListAsync();
        lock (_lock)
        {
            foreach (var id in _watched.Keys.Where(id => folders.All(f => f.Id != id)).ToList())
            {
                _watched[id].Watcher.Dispose();
                _watched[id].Debounce.Dispose();
                _watched.Remove(id);
            }
        }
        foreach (var folder in folders)
        {
            await ScanAsync(folder);
            Watch(folder);
        }
    }

    private void Watch(LibraryFolder folder)
    {
        lock (_lock)
        {
            if (_disposed || _watched.ContainsKey(folder.Id) || !Directory.Exists(folder.Path)) return;
            try
            {
                var watcher = new FileSystemWatcher(folder.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
                var debounce = new Timer(_ => _ = ScanAsync(folder), null, Timeout.Infinite, Timeout.Infinite);
                void Poke(object s, FileSystemEventArgs e) => debounce.Change(Quiet, Timeout.InfiniteTimeSpan);
                watcher.Created += Poke;
                watcher.Changed += Poke;
                watcher.Deleted += Poke;
                watcher.Renamed += (s, e) => Poke(s, e);
                watcher.Error += (_, _) => debounce.Change(Quiet, Timeout.InfiniteTimeSpan);
                watcher.EnableRaisingEvents = true;
                _watched[folder.Id] = (watcher, debounce);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Not watchable (e.g. network share without notifications): covered by the periodic check.
            }
        }
    }

    private async Task ScanAsync(LibraryFolder folder)
    {
        if (_disposed) return;
        try
        {
            var result = await Task.Run(() => _scanner.ScanFolderAsync(folder));
            lock (_lock) _available[folder.Id] = result.Available;
            if (result.Added + result.Updated + result.Removed > 0 || !result.Available) Changed?.Invoke();
            if (!result.Available)
            {
                lock (_lock)
                {
                    if (_watched.Remove(folder.Id, out var w))
                    {
                        w.Watcher.Dispose();
                        w.Debounce.Dispose();
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    private async Task CheckAvailabilityAsync()
    {
        foreach (var folder in await _library.Folders.ListAsync())
        {
            bool was;
            lock (_lock) was = _available.GetValueOrDefault(folder.Id, true);
            if (Directory.Exists(folder.Path) == was) continue;
            await ScanAsync(folder);
            Watch(folder);
            Changed?.Invoke();
        }
    }

    /// <summary>Page size with the Windows decoder (formats the header reader does not know, e.g. AVIF).</summary>
    private static (int Width, int Height)? MeasureWithDecoder(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _availability.Dispose();
            foreach (var (watcher, debounce) in _watched.Values)
            {
                watcher.Dispose();
                debounce.Dispose();
            }
            _watched.Clear();
        }
    }
}
