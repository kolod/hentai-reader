using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;
using HentaiReader.Host.Library;
using HentaiReader.Host.Tests.Downloads;
using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Tests.Library;

public sealed class LibraryFoldersTests : IAsyncDisposable
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("hr-folders-").FullName;

    /// <summary>A saved gallery with one page; <paramref name="width"/> stands for the image quality.</summary>
    private async Task<string> SaveAsync(string folder, string title, string id, int width = 100)
    {
        var page = Path.Combine(_dir, $"{id}-{width}.png");
        await File.WriteAllBytesAsync(page, DownloadQueueTests.Png(width, 150), Token);
        return await CbzWriter.WriteAsync(folder, [page], new ArchiveMetadata
        {
            Source = new() { Id = "hitomi", GalleryId = id },
            Title = title,
            SavedAt = DateTimeOffset.UtcNow,
            Pages = [new("0001.png", width, 150)],
        }, Token);
    }

    /// <summary>Folder a with gallery 1, folder b with <paramref name="inB"/> already saved, both scanned.</summary>
    private async Task<(LibraryService Library, LibraryFolders Folders, long A, long B)> TwoFoldersAsync(Func<string, Task> inB, List<string> discarded)
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        var library = await LibraryService.OpenAsync(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "none.json"), a, token: Token);
        var folders = new LibraryFolders(library, path => { discarded.Add(path); File.Delete(path); });
        await SaveAsync(a, "One", "1");
        await inB(b);
        var fb = await folders.AddAsync(b, Token);
        await new LibraryScanner(library).ScanAllAsync(Token);
        var fa = (await folders.ListAsync(Token)).First(f => f.Id != fb.Id);
        return (library, folders, fa.Id, fb.Id);
    }

    [Fact]
    public async Task Identical_pages_discard_the_incoming_copy_without_asking()
    {
        var discarded = new List<string>();
        var (library, folders, a, b) = await TwoFoldersAsync(b => SaveAsync(b, "One", "1"), discarded);

        await folders.MoveAndRemoveAsync(a, b, resolve: _ => throw new InvalidOperationException("asked"), token: Token);

        var item = Assert.Single(await library.Archives.ListAsync(new(), Token));
        Assert.Equal((b, "One.cbz"), (item.Archive.FolderId, item.Archive.RelPath));
        Assert.Equal([Path.Combine(_dir, "a", "One.cbz")], discarded);
    }

    [Fact]
    public async Task Identical_pages_under_another_name_count_as_the_same_gallery()
    {
        var discarded = new List<string>();
        var (library, folders, a, b) = await TwoFoldersAsync(async b =>
        {
            var file = await SaveAsync(b, "One", "1");
            File.Move(file, Path.Combine(b, "Renamed.cbz"));
        }, discarded);

        await folders.MoveAndRemoveAsync(a, b, resolve: _ => throw new InvalidOperationException("asked"), token: Token);

        Assert.Equal("Renamed.cbz", Assert.Single(await library.Archives.ListAsync(new(), Token)).Archive.RelPath);
        Assert.Single(discarded);
    }

    [Theory]
    [InlineData(ConflictChoice.KeepBoth, new[] { "One (2).cbz:100", "One.cbz:200" }, 0)]
    [InlineData(ConflictChoice.Replace, new[] { "One.cbz:100" }, 1)]
    [InlineData(ConflictChoice.KeepExisting, new[] { "One.cbz:200" }, 1)]
    public async Task Different_pages_ask_which_copy_stays(ConflictChoice choice, string[] expected, int discards)
    {
        var discarded = new List<string>();
        var (library, folders, a, b) = await TwoFoldersAsync(b => SaveAsync(b, "One", "1", width: 200), discarded);
        var asked = new List<MoveConflict>();

        await folders.MoveAndRemoveAsync(a, b, resolve: c => { asked.Add(c); return Task.FromResult(choice); }, token: Token);

        var conflict = Assert.Single(asked);
        Assert.Equal((Path.Combine(_dir, "b", "One.cbz"), Path.Combine(_dir, "a", "One.cbz")), (conflict.Existing.Path, conflict.Incoming.Path));
        Assert.Equal(("One", 1), (conflict.Incoming.Title, conflict.Incoming.PageCount));
        var items = await library.Archives.ListAsync(new(), Token);
        Assert.Equal(expected, items.Select(i => $"{i.Archive.RelPath}:{i.Archive.Pages[0].Width}").Order());
        Assert.All(items, i => Assert.True(File.Exists(i.FullPath)));
        Assert.Equal(discards, discarded.Count);
        Assert.Equal([b], (await folders.ListAsync(Token)).Select(f => f.Id));
    }

    [Fact]
    public async Task Move_keeps_galleries_readable_and_listed()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        var library = await LibraryService.OpenAsync(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "none.json"), a, token: Token);
        var folders = new LibraryFolders(library);
        var scanner = new LibraryScanner(library);
        await SaveAsync(a, "One", "1");
        await SaveAsync(Path.Combine(a, "sub"), "Two", "2");
        await SaveAsync(b, "One", "3", width: 200);   // same file name already in b, another gallery
        var fb = await folders.AddAsync(b, Token);
        await scanner.ScanAllAsync(Token);
        var fa = (await folders.ListAsync(Token)).First(f => f.Path.EndsWith("a"));
        var reports = new List<(int, int)>();

        await folders.MoveAndRemoveAsync(fa.Id, fb.Id, new SyncProgress(reports), token: Token);

        Assert.Equal([fb.Id], (await folders.ListAsync(Token)).Select(f => f.Id));
        Assert.True((await folders.ListAsync(Token)).Single().IsTarget);
        Assert.Equal([(1, 2), (2, 2)], reports);
        var items = await library.Archives.ListAsync(new(Sort: LibrarySort.Title), Token);
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.True(File.Exists(i.FullPath), i.FullPath));
        Assert.Contains(items, i => i.Archive.RelPath == "One (2).cbz");
        Assert.Contains(items, i => i.Archive.RelPath == Path.Combine("sub", "Two.cbz"));
        Assert.NotNull(await library.FindSavedAsync(new GalleryKey("hitomi", "1"), Token));
        var unchanged = await scanner.ScanFolderAsync((await folders.ListAsync(Token)).Single(), Token);
        Assert.Equal((0, 0, 3), (unchanged.Added, unchanged.Removed, unchanged.Unchanged));
    }

    [Fact]
    public async Task Move_survives_the_destination_watcher_scanning_the_file_first()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        var library = await LibraryService.OpenAsync(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "none.json"), a, token: Token);
        var folders = new LibraryFolders(library);
        var scanner = new LibraryScanner(library);
        var file = await SaveAsync(a, "One", "1");
        var fb = await folders.AddAsync(b, Token);
        await scanner.ScanAllAsync(Token);
        var row = (await library.Archives.ListAsync(new(), Token)).Single().Archive;
        await library.Archives.SetDirtyAsync(row.Id, true, Token);
        // The file lands in b and b's watcher scans it in before the row is moved.
        Directory.CreateDirectory(b);
        File.Move(file, Path.Combine(b, "One.cbz"));
        await scanner.ScanFolderAsync((await folders.ListAsync(Token)).Single(f => f.Id == fb.Id), Token);

        await library.Archives.MoveAsync(row.Id, fb.Id, "One.cbz", Token);

        var moved = (await library.Archives.ListAsync(new(), Token)).Single().Archive;
        Assert.Equal((row.Id, fb.Id, "One.cbz", true), (moved.Id, moved.FolderId, moved.RelPath, moved.MetadataDirty));
    }

    [Fact]
    public async Task Keep_files_only_removes_rows_and_the_last_folder_stays()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        var library = await LibraryService.OpenAsync(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "none.json"), a, token: Token);
        var folders = new LibraryFolders(library);
        var file = await SaveAsync(b, "Kept", "1");
        var fb = await folders.AddAsync(b, Token);
        await new LibraryScanner(library).ScanAllAsync(Token);

        await folders.RemoveKeepFilesAsync(fb.Id, Token);

        Assert.True(File.Exists(file));
        Assert.Empty(await library.Archives.ListAsync(new(), Token));
        var last = (await folders.ListAsync(Token)).Single();
        Assert.Equal("last", await folders.CannotRemoveAsync(last.Id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.RemoveKeepFilesAsync(last.Id, Token));
    }

    [Fact]
    public async Task Folder_with_unfinished_downloads_is_not_removed()
    {
        var a = Path.Combine(_dir, "a");
        var library = await LibraryService.OpenAsync(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "none.json"), a, token: Token);
        var folders = new LibraryFolders(library);
        var fb = await folders.AddAsync(Path.Combine(_dir, "b"), Token);
        await folders.SetTargetAsync(fb.Id, Token);
        await library.Downloads.AddAsync(new DownloadRow { Key = new("hitomi", "1"), Title = "G", GalleryJson = "{}", FolderId = fb.Id, PageCount = 3, QueuedAt = library.Now }, Token);

        Assert.Equal("downloads", await folders.CannotRemoveAsync(fb.Id, Token));
    }

    private sealed class SyncProgress(List<(int, int)> reports) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => reports.Add(value);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
