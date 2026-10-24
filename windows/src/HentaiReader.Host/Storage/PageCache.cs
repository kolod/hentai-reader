using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HentaiReader.Host.Storage;

/// <summary>
/// Disk cache of downloaded page images (specs/005-windows-reader/data-model.md "Page cache").
/// Files are named by the SHA-256 of their key, so the index (name → size, last use) can always be
/// rebuilt from the folder. Least recently used files are evicted beyond the limit. Thread-safe.
/// </summary>
public sealed class PageCache : IDisposable
{
    private const string IndexName = "index.json";
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private readonly string _folder;
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
    }

    public PageCache(string folder, long limitBytes, TimeProvider? time = null)
    {
        _folder = folder;
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
                bytes = File.ReadAllBytes(Path.Combine(_folder, name));
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
            if (_entries.TryGetValue(name, out var old)) _size -= old.Size;
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
            var data = _entries.ToDictionary(e => e.Key, e => new[] { e.Value.Size, e.Value.LastUsed.ToUnixTimeMilliseconds() });
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, Path.Combine(_folder, IndexName), overwrite: true);
            _dirty = false;
            _lastFlush = _time.GetUtcNow();
        }
    }

    public void Dispose() => Flush();

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
            TryDelete(Path.Combine(_folder, name));
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
                if (v.Length == 2 && File.Exists(Path.Combine(_folder, name)))
                    entries[name] = new Entry { Size = v[0], LastUsed = DateTimeOffset.FromUnixTimeMilliseconds(v[1]) };
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
