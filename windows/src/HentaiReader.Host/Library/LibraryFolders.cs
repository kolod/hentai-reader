using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;

namespace HentaiReader.Host.Library;

/// <summary>A saved gallery as shown when two copies meet during a move.</summary>
public sealed record ArchiveSummary(string Path, string Title, int PageCount, long Size, DateTimeOffset ModifiedAt);

/// <summary>The destination already has this gallery or this file name, with different pages.</summary>
public sealed record MoveConflict(ArchiveSummary Existing, ArchiveSummary Incoming);

public enum ConflictChoice
{
    /// <summary>The incoming copy is moved under a free name (<c>name (2).cbz</c>).</summary>
    KeepBoth,
    /// <summary>The existing copy is discarded and the incoming one takes its place.</summary>
    Replace,
    /// <summary>The incoming copy is discarded.</summary>
    KeepExisting,
}

/// <summary>
/// Library folders (US4): add, choose the target for new saves, remove with "move galleries" (into
/// another library folder, keeping their relative paths) or "keep files". At least one folder always
/// remains; a folder with unfinished downloads is not removed. <paramref name="discard"/> deletes a
/// duplicate file (the app passes the Recycle Bin); by default it is deleted outright.
/// </summary>
public sealed class LibraryFolders(LibraryService library, Action<string>? discard = null)
{
    private readonly Action<string> _discard = discard ?? File.Delete;

    public Task<IReadOnlyList<LibraryFolder>> ListAsync(CancellationToken token = default) => library.Folders.ListAsync(token);

    public Task<LibraryFolder> AddAsync(string path, CancellationToken token = default) => library.Folders.AddAsync(path, library.Now, token);

    public Task SetTargetAsync(long id, CancellationToken token = default) => library.Folders.SetTargetAsync(id, token);

    /// <summary>Why a folder cannot be removed now, or null.</summary>
    public async Task<string?> CannotRemoveAsync(long id, CancellationToken token = default)
    {
        if ((await library.Folders.ListAsync(token)).Count <= 1) return "last";
        var busy = (await library.Downloads.ListAsync(token)).Any(d => d.FolderId == id && d.State != DownloadState.Done);
        return busy ? "downloads" : null;
    }

    /// <summary>The folder leaves the library; its files stay where they are.</summary>
    public async Task RemoveKeepFilesAsync(long id, CancellationToken token = default)
    {
        if (await CannotRemoveAsync(id, token) is { } reason) throw new InvalidOperationException($"folder cannot be removed: {reason}");
        await library.Folders.RemoveAsync(id, token);
    }

    /// <summary>
    /// Moves every saved gallery of the folder into <paramref name="targetId"/> (same relative paths),
    /// then removes the folder. When the destination already has the same file name or the same
    /// gallery: identical pages → the incoming copy is discarded without asking; different pages →
    /// <paramref name="resolve"/> decides (without it both are kept). A gallery whose move fails stays
    /// where it is, and the folder is not removed (the method throws after moving the rest).
    /// </summary>
    public async Task MoveAndRemoveAsync(long id, long targetId, IProgress<(int Done, int Total)>? progress = null,
        Func<MoveConflict, Task<ConflictChoice>>? resolve = null, CancellationToken token = default)
    {
        if (await CannotRemoveAsync(id, token) is { } reason) throw new InvalidOperationException($"folder cannot be removed: {reason}");
        var folders = await library.Folders.ListAsync(token);
        var from = folders.First(f => f.Id == id);
        var to = folders.First(f => f.Id == targetId);
        var archives = await library.Archives.InFolderAsync(id, token);
        var failed = new List<string>();
        var done = 0;
        foreach (var archive in archives)
        {
            token.ThrowIfCancellationRequested();
            var source = Path.Combine(from.Path, archive.RelPath);
            try
            {
                if (File.Exists(source))
                {
                    var destination = Path.Combine(to.Path, archive.RelPath);
                    var existing = File.Exists(destination) && !SamePath(destination, source) ? destination : await SameGalleryAsync(archive.Key, to, source, token);
                    if (existing is not null)
                    {
                        var (incoming, incomingId) = await Task.Run(() => Inspect(source), token);
                        var (present, presentId) = await Task.Run(() => Inspect(existing), token);
                        var choice = incomingId is not null && incomingId == presentId ? ConflictChoice.KeepExisting
                            : resolve is null ? ConflictChoice.KeepBoth
                            : await resolve(new MoveConflict(present, incoming));
                        if (choice == ConflictChoice.KeepExisting)
                        {
                            await Task.Run(() => _discard(source), token);
                            await library.Archives.DeleteAsync(archive.Id, token);
                            progress?.Report((++done, archives.Count));
                            continue;
                        }
                        if (choice == ConflictChoice.Replace)
                        {
                            await Task.Run(() => _discard(existing), token);
                            destination = existing;   // its row is dropped by MoveAsync
                        }
                    }
                    destination = Free(destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await Task.Run(() => File.Move(source, destination), token);
                    await library.Archives.MoveAsync(archive.Id, targetId, Path.GetRelativePath(to.Path, destination), token);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add($"{archive.RelPath}: {e.Message}");
            }
            progress?.Report((++done, archives.Count));
        }
        if (failed.Count > 0) throw new IOException("some galleries were not moved:\n" + string.Join("\n", failed));
        await library.Folders.RemoveAsync(id, token);
    }

    /// <summary>
    /// Another file of the same gallery already in <paramref name="folder"/>, or null. Never
    /// <paramref name="source"/> itself (nested library folders list one file twice).
    /// </summary>
    private async Task<string?> SameGalleryAsync(GalleryKey key, LibraryFolder folder, string source, CancellationToken token)
    {
        foreach (var item in await library.Archives.FindAsync(key, token))
        {
            if (item.Archive.FolderId == folder.Id && File.Exists(item.FullPath) && !SamePath(item.FullPath, source)) return item.FullPath;
        }
        return null;
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>What the user sees of a copy, and its page fingerprint (null when unreadable: never "identical").</summary>
    private static (ArchiveSummary Summary, string? ContentId) Inspect(string path)
    {
        var info = new FileInfo(path);
        try
        {
            using var reader = CbzReader.Open(path);
            var title = reader.ReadMetadata()?.Title ?? Path.GetFileNameWithoutExtension(path);
            return (new ArchiveSummary(path, title, reader.Pages.Count, info.Length, info.LastWriteTimeUtc), reader.Pages.Count > 0 ? reader.ContentId() : null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return (new ArchiveSummary(path, Path.GetFileNameWithoutExtension(path), 0, info.Length, info.LastWriteTimeUtc), null);
        }
    }

    /// <summary><c>name.cbz</c> or <c>name (2).cbz</c>… when taken.</summary>
    private static string Free(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
