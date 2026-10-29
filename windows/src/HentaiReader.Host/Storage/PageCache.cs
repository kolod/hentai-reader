using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HentaiReader.Host.Storage;

/// <summary>
/// Disk cache of downloaded page images (specs/005-windows-reader/data-model.md "Page cache").
/// Files are named by the SHA-256 of their key, so the index (name → size, last use) can always be
/// rebuilt from the folder. Least recently used files are evicted beyond the limit. Thread-safe.
/// The cache can move to another folder while in use (006 US7, research R10): during a move each file
/// is read from wherever it is, new files go to the new folder, and <see cref="MoveStepAsync"/> moves
/// the rest one by one.
/// </summary>
public sealed class PageCache : IDisposable
{
    private const string IndexName = "index.json";
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private string _folder;
    /// <summary>Folder being moved away from (null when no move runs).</summary>
    private string? _old;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries;
    private long _limit;
    private long _size;
    private bool _dirty;
    private DateTimeOffset _lastFlush;

    private sealed class Entry
    {
        public long Size { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        /// <summary>The file is still in the folder being moved away from.</summary>
        public bool InOld { get; set; }
    }

    /// <param name="moveFrom">The folder of an unfinished move (it continues with <see cref="MoveStepAsync"/>).</param>
    public PageCache(string folder, long limitBytes, TimeProvider? time = null, string? moveFrom = null)
    {
        _folder = folder;
        _old = moveFrom is not null && Directory.Exists(moveFrom) && !SameFolder(moveFrom, folder) ? moveFrom : null;
        _time = time ?? TimeProvider.System;
        _limit = limitBytes;
        Directory.CreateDirectory(folder);
        _entries = LoadIndex() ?? Rebuild();
        _size = _entries.Values.Sum(e => e.Size);
        _lastFlush = _time.GetUtcNow();
        lock (_lock) Evict();
    }

    /// <summary>Cache key of a page; the fingerprint separates different setting values (e.g. image quality).</summary>
    public static string Key(string sourceId, string galleryId, int page, string fingerprint) =>
        $"{sourceId}|{galleryId}|{page}|{fingerprint}";

    /// <summary>Stable fingerprint of a source's setting values (order-independent).</summary>
    public static string Fingerprint(IReadOnlyDictionary<string, string>? settings)
    {
        var text = string.Join('\n', (settings ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    public long SizeBytes
    {
        get { lock (_lock) return _size; }
    }

    public long LimitBytes
    {
        get { lock (_lock) return _limit; }
        set
        {
            lock (_lock)
            {
                _limit = value;
                Evict();
            }
        }
    }

    public bool TryGet(string key, out byte[] bytes)
    {
        var name = FileName(key);
        lock (_lock)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                bytes = [];
                return false;
            }
            try
            {
                bytes = File.ReadAllBytes(PathOf(name, entry));
            }
            catch (IOException)
            {
                Forget(name, entry);
                bytes = [];
                return false;
            }
            entry.LastUsed = _time.GetUtcNow();
            Touch();
            return true;
        }
    }

    public async Task PutAsync(string key, byte[] bytes, CancellationToken token = default)
    {
        var name = FileName(key);
        var path = Path.Combine(_folder, name);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temp, bytes, token);
        lock (_lock)
        {
            File.Move(temp, path, overwrite: true);
            if (_entries.TryGetValue(name, out var old))
            {
                _size -= old.Size;
                if (old.InOld) TryDelete(PathOf(name, old));
            }
            _entries[name] = new Entry { Size = bytes.Length, LastUsed = _time.GetUtcNow() };
            _size += bytes.Length;
            Evict();
            Touch();
        }
    }

    /// <summary>Deletes every cached image and the index; nothing else in the data folder.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var file in Directory.EnumerateFiles(_folder)) TryDelete(file);
            if (_old is not null && Directory.Exists(_old)) foreach (var file in Directory.EnumerateFiles(_old)) TryDelete(file);
            _entries.Clear();
            _size = 0;
            _dirty = false;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            var temp = Path.Combine(_folder, IndexName + ".tmp");
            var data = _entries.ToDictionary(e => e.Key, e => new[] { e.Value.Size, e.Value.LastUsed.ToUnixTimeMilliseconds(), e.Value.InOld ? 1L : 0L });
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(data));
                File.Move(temp, Path.Combine(_folder, IndexName), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Folder gone or full: the index is rebuilt from the files next time.
                return;
            }
            _dirty = false;
            _lastFlush = _time.GetUtcNow();
        }
    }

    public void Dispose() => Flush();

    /// <summary>Current folder (new files go here).</summary>
    public string Folder
    {
        get { lock (_lock) return _folder; }
    }

    /// <summary>Folder still being moved away from, or null.</summary>
    public string? MovingFrom
    {
        get { lock (_lock) return _old; }
    }

    /// <summary>Files still to move, and all files.</summary>
    public (int Remaining, int Total) MoveProgress
    {
        get { lock (_lock) return (_entries.Values.Count(e => e.InOld), _entries.Count); }
    }

    /// <summary>
    /// Starts moving the cache to <paramref name="folder"/>: new files go there at once, existing ones are
    /// moved by <see cref="MoveStepAsync"/>. Calling it with the previous folder during a move turns the
    /// move around (cancel: files go back).
    /// </summary>
    public void BeginMove(string folder)
    {
        lock (_lock)
        {
            if (SameFolder(folder, _folder)) return;
            Directory.CreateDirectory(folder);
            var reverse = _old is not null && SameFolder(folder, _old);
            if (_old is not null && !reverse) throw new InvalidOperationException("a cache move is already running");
            _old = _folder;
            _folder = folder;
            foreach (var entry in _entries.Values) entry.InOld = !reverse || !entry.InOld;
            _dirty = true;
            Flush();
        }
    }

    /// <summary>
    /// Moves up to <paramref name="count"/> files to the current folder; returns false when nothing is left
    /// (the move is then complete and the old folder cleaned up). An I/O failure (no space, folder gone)
    /// is thrown and loses nothing: unmoved files stay readable where they are.
    /// </summary>
    public async Task<bool> MoveStepAsync(int count = 20, CancellationToken token = default)
    {
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            string name, from, to;
            lock (_lock)
            {
                var next = _entries.FirstOrDefault(e => e.Value.InOld);
                if (next.Key is null)
                {
                    Complete();
                    return false;
                }
                name = next.Key;
                from = Path.Combine(_old!, name);
                to = Path.Combine(_folder, name);
            }
            if (File.Exists(from)) await Task.Run(() => File.Move(from, to, overwrite: true), token);
            lock (_lock)
            {
                if (_entries.TryGetValue(name, out var entry) && entry.InOld)
                {
                    if (File.Exists(to)) entry.InOld = false;
                    else Forget(name, entry);   // vanished from the old folder meanwhile
                }
                Touch();
            }
        }
        lock (_lock)
        {
            if (_entries.Values.Any(e => e.InOld)) return true;
            Complete();
            return false;
        }
    }

    private void Complete()
    {
        if (_old is null) return;
        TryDelete(Path.Combine(_old, IndexName));
        try
        {
            if (Directory.Exists(_old) && !Directory.EnumerateFileSystemEntries(_old).Any()) Directory.Delete(_old);
        }
        catch (IOException)
        {
        }
        _old = null;
        _dirty = true;
        Flush();
    }

    private string PathOf(string name, Entry entry) => Path.Combine(entry.InOld && _old is not null ? _old : _folder, name);

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string FileName(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private void Touch()
    {
        _dirty = true;
        if (_time.GetUtcNow() - _lastFlush >= FlushInterval) Flush();
    }

    private void Evict()
    {
        if (_size <= _limit) return;
        foreach (var (name, entry) in _entries.OrderBy(e => e.Value.LastUsed).ToList())
        {
            if (_size <= _limit) break;
            TryDelete(PathOf(name, entry));
            Forget(name, entry);
        }
    }

    private void Forget(string name, Entry entry)
    {
        _entries.Remove(name);
        _size -= entry.Size;
        _dirty = true;
    }

    private Dictionary<string, Entry>? LoadIndex()
    {
        try
        {
            var path = Path.Combine(_folder, IndexName);
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<Dictionary<string, long[]>>(File.ReadAllText(path));
            if (data is null) return null;
            var entries = new Dictionary<string, Entry>();
            foreach (var (name, v) in data)
            {
                // Files deleted behind our back are dropped; the index never lists missing files.
                if (v.Length < 2) continue;
                var entry = new Entry { Size = v[0], LastUsed = DateTimeOffset.FromUnixTimeMilliseconds(v[1]), InOld = v.Length > 2 && v[2] == 1 && _old is not null };
                if (File.Exists(PathOf(name, entry))) entries[name] = entry;
                else if (entry.InOld && File.Exists(Path.Combine(_folder, name))) entries[name] = new Entry { Size = entry.Size, LastUsed = entry.LastUsed };
            }
            return entries;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private Dictionary<string, Entry> Rebuild()
    {
        var entries = new Dictionary<string, Entry>();
        if (_old is not null)
        {
            foreach (var file in new DirectoryInfo(_old).EnumerateFiles())
            {
                if (file.Name.EndsWith(".tmp", StringComparison.Ordinal) || file.Name.StartsWith(IndexName, StringComparison.Ordinal)) continue;
                entries[file.Name] = new Entry { Size = file.Length, LastUsed = file.LastWriteTimeUtc, InOld = true };
            }
        }
        foreach (var file in new DirectoryInfo(_folder).EnumerateFiles())
        {
            if (file.Name.EndsWith(".tmp", StringComparison.Ordinal) || file.Name.StartsWith(IndexName, StringComparison.Ordinal))
            {
                TryDelete(file.FullName);
                continue;
            }
            entries[file.Name] = new Entry { Size = file.Length, LastUsed = file.LastWriteTimeUtc };
        }
        _dirty = true;
        return entries;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // In use or gone: the index no longer lists it either way.
        }
    }
}
