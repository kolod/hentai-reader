using System.Text;
using System.Text.Json;
using HentaiReader.Host.Engine;

namespace HentaiReader.Host.Storage;

/// <summary>
/// Per-source key-value storage with the contract limits; in memory, optionally persisted to a
/// JSON file (atomic replace) so it survives restarts (FR-005).
/// </summary>
public sealed class SourceStorage
{
    public const int KeyChars = 128;
    public const int ValueBytes = 1024 * 1024;
    public const int TotalBytes = 8 * 1024 * 1024;

    private readonly Dictionary<string, string> _data = new();
    private readonly string? _file;
    private readonly object _lock = new();

    public SourceStorage(string? file = null)
    {
        _file = file;
        if (file is not null && File.Exists(file))
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (loaded is not null) _data = loaded;
        }
    }

    public void Seed(IReadOnlyDictionary<string, string> values)
    {
        lock (_lock) foreach (var (k, v) in values) _data[k] = v;
    }

    public Task<string> GetAsync(string key)
    {
        if (CheckKey(key) is { } bad) return Task.FromResult(bad);
        lock (_lock) return Task.FromResult(Envelope.Value(_data.TryGetValue(key, out var v) ? v : null));
    }

    public async Task<string> SetAsync(string key, string valueJson)
    {
        if (CheckKey(key) is { } bad) return bad;
        var value = JsonSerializer.Deserialize<string?>(valueJson);
        string? snapshot;
        lock (_lock)
        {
            if (value is null)
            {
                _data.Remove(key);
            }
            else
            {
                var size = Encoding.UTF8.GetByteCount(value);
                if (size > ValueBytes) return Envelope.Error(SourceErrorKind.InvalidInput, $"storage.set: value for '{key}' exceeds {ValueBytes} bytes");
                var total = size + Encoding.UTF8.GetByteCount(key) +
                            _data.Where(kv => kv.Key != key).Sum(kv => Encoding.UTF8.GetByteCount(kv.Key) + Encoding.UTF8.GetByteCount(kv.Value));
                if (total > TotalBytes) return Envelope.Error(SourceErrorKind.InvalidInput, $"storage.set: source storage exceeds {TotalBytes} bytes");
                _data[key] = value;
            }
            snapshot = _file is null ? null : JsonSerializer.Serialize(_data);
        }
        if (snapshot is not null) await WriteAtomicAsync(_file!, snapshot);
        return Envelope.Value(null);
    }

    private static string? CheckKey(string key) =>
        key.Length is 0 or > KeyChars ? Envelope.Error(SourceErrorKind.InvalidInput, $"storage: key must be 1–{KeyChars} characters") : null;

    private static async Task WriteAtomicAsync(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, text);
        File.Move(temp, file, overwrite: true);
    }
}
