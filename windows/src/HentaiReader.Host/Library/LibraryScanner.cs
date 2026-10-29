using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;
using HentaiReader.Host.Downloads;

namespace HentaiReader.Host.Library;

/// <summary>
/// Finds saved galleries in the library folders (US3, research R7): archives with this app's metadata
/// are listed with it; other comic archives by file name, with page sizes measured once from the image
/// headers; damaged files are listed as unreadable; a missing folder marks its galleries unavailable.
/// Unchanged files (same size and time) are not reopened.
/// </summary>
public sealed class LibraryScanner(LibraryService library)
{
    private const int HeaderBytes = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Measures a page the header reader does not understand (e.g. AVIF): entry bytes → size. Set by the
    /// app (platform decoder); without it such pages get no size and the reader measures them on decode.
    /// </summary>
    public Func<byte[], (int Width, int Height)?>? Measure { get; init; }

    public sealed record Result(int Added, int Updated, int Removed, int Unchanged, bool Available);

    public async Task ScanAllAsync(CancellationToken token = default)
    {
        foreach (var folder in await library.Folders.ListAsync(token)) await ScanFolderAsync(folder, token);
    }

    public async Task<Result> ScanFolderAsync(LibraryFolder folder, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!Directory.Exists(folder.Path))
            {
                await library.Archives.SetFolderAvailableAsync(folder.Id, available: false, token);
                return new Result(0, 0, 0, 0, Available: false);
            }
            await library.Archives.SetFolderAvailableAsync(folder.Id, available: true, token);
            var known = (await library.Archives.InFolderAsync(folder.Id, token)).ToDictionary(a => a.RelPath, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int added = 0, updated = 0, unchanged = 0;
            foreach (var file in await Task.Run(() => ArchiveFiles(folder.Path).ToList(), token))
            {
                token.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(folder.Path, file);
                seen.Add(rel);
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    _ = info.Length;
                }
                catch (IOException)
                {
                    continue;
                }
                if (known.TryGetValue(rel, out var row) && row.Size == info.Length && Math.Abs((row.ModifiedAt - info.LastWriteTimeUtc).TotalSeconds) < 2)
                {
                    unchanged++;
                    continue;
                }
                var fresh = await Task.Run(() => Read(folder, file, info), token);
                await library.Archives.UpsertAsync(fresh with { MetadataDirty = row?.MetadataDirty ?? false }, token);
                if (row is null) added++;
                else updated++;
            }
            var removed = 0;
            foreach (var row in known.Values.Where(r => !seen.Contains(r.RelPath)))
            {
                await library.Archives.DeleteAsync(row.Id, token);
                removed++;
            }
            return new Result(added, updated, removed, unchanged, Available: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Archives under a folder, skipping the download staging area and half-written archives.</summary>
    public static IEnumerable<string> ArchiveFiles(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        return Directory.EnumerateFiles(folder, "*", options)
            .Where(f => f.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Contains(DownloadQueue.StagingFolder));
    }

    private ArchiveRow Read(LibraryFolder folder, string file, FileInfo info)
    {
        try
        {
            using var reader = CbzReader.Open(file);
            if (reader.ReadMetadata() is { } metadata) return LibraryService.RowFor(folder.Id, folder.Path, file, metadata);
            if (reader.Pages.Count == 0) throw new InvalidDataException("no page images");
            var pages = reader.Pages.Select(entry =>
            {
                var size = ImageFormat.TryGetSize(reader.ReadEntryPrefix(entry, HeaderBytes));
                if (size is null && Measure is { } measure) size = measure(reader.ReadEntry(entry));
                return new ArchivePage(entry.Name, size?.Width ?? 0, size?.Height ?? 0);
            }).ToList();
            return new ArchiveRow
            {
                FolderId = folder.Id,
                RelPath = Path.GetRelativePath(folder.Path, file),
                Size = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
                Key = new GalleryKey(GalleryKey.LocalSource, reader.ContentId()),
                Title = Path.GetFileNameWithoutExtension(file),
                Pages = pages,
                SavedAt = info.CreationTimeUtc,
                Format = "foreign",
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return new ArchiveRow
            {
                FolderId = folder.Id,
                RelPath = Path.GetRelativePath(folder.Path, file),
                Size = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
                Key = new GalleryKey(GalleryKey.LocalSource, "unreadable:" + Path.GetRelativePath(folder.Path, file)),
                Title = Path.GetFileNameWithoutExtension(file),
                Pages = [],
                SavedAt = info.CreationTimeUtc,
                State = ArchiveState.Unreadable,
                Format = "foreign",
            };
        }
    }
}
