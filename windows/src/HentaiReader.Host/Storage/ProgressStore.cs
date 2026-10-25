using System.Text.Json;

namespace HentaiReader.Host.Storage;

/// <summary>How far a gallery was read (specs/005-windows-reader data-model.md "Reading progress").</summary>
public sealed record ReadingProgress(int Page, double Offset, int PageCount, bool Read, DateTimeOffset UpdatedAt, string Title);

/// <summary>
/// Reading progress per source and gallery in one JSON file, written atomically. A gallery once
/// read to the end stays marked read. Thread-safe.
/// </summary>
public sealed class ProgressStore(string path)
{
    private readonly object _lock = new();
    private Dictionary<string, ReadingProgress>? _entries;

    public static string Key(string sourceId, string galleryId) => $"{sourceId}|{galleryId}";

    public ReadingProgress? Get(string sourceId, string galleryId)
    {
        lock (_lock) return Entries.GetValueOrDefault(Key(sourceId, galleryId));
    }

    public void Set(string sourceId, string galleryId, ReadingProgress progress)
    {
        lock (_lock)
        {
            var key = Key(sourceId, galleryId);
            var read = progress.Read || (Entries.TryGetValue(key, out var old) && old.Read);
            Entries[key] = progress with
            {
                Page = Math.Clamp(progress.Page, 0, Math.Max(0, progress.PageCount - 1)),
                Offset = Math.Clamp(progress.Offset, 0, 1),
                Read = read,
            };
            Save();
        }
    }

    private Dictionary<string, ReadingProgress> Entries => _entries ??= Load();

    private Dictionary<string, ReadingProgress> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, ReadingProgress>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            // A damaged file must not stop reading; progress starts over.
            return new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
        File.Move(temp, path, overwrite: true);
    }
}
