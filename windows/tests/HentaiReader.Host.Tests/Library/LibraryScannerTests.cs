using System.IO.Compression;
using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;
using HentaiReader.Host.Downloads;
using HentaiReader.Host.Library;
using HentaiReader.Host.Tests.Downloads;
using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Tests.Library;

public sealed class LibraryScannerTests : IAsyncDisposable
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("hr-scan-").FullName;

    private string Lib => Path.Combine(_dir, "lib");

    private async Task<LibraryService> OpenAsync() =>
        await LibraryService.OpenAsync(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "none.json"), Lib, token: Token);

    private async Task<string> SavedAsync(string title, string galleryId)
    {
        var files = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            files.Add(Path.Combine(_dir, $"{galleryId}-{i}.png"));
            await File.WriteAllBytesAsync(files[^1], DownloadQueueTests.Png(300 + i, 400), Token);
        }
        var metadata = new ArchiveMetadata
        {
            Source = new() { Id = "hitomi", GalleryId = galleryId },
            Title = title,
            SavedAt = DateTimeOffset.UtcNow,
            Pages = [new("0001.png", 300, 400), new("0002.png", 301, 400)],
        };
        return await CbzWriter.WriteAsync(Lib, files, metadata, Token);
    }

    private string Foreign(string relPath)
    {
        var path = Path.Combine(Lib, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, w) in new[] { ("10.png", 110), ("2.png", 102) })
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            s.Write(DownloadQueueTests.Png(w, 200));
        }
        return path;
    }

    [Fact]
    public async Task Finds_saved_foreign_and_damaged_archives_and_tracks_changes()
    {
        var library = await OpenAsync();
        var scanner = new LibraryScanner(library);
        var saved = await SavedAsync("Mine", "1");
        Foreign(Path.Combine("sub", "Other comic.cbz"));
        await File.WriteAllTextAsync(Path.Combine(Lib, "broken.cbz"), "not a zip", Token);
        Directory.CreateDirectory(Path.Combine(Lib, DownloadQueue.StagingFolder, "partial", "9"));
        await File.WriteAllBytesAsync(Path.Combine(Lib, DownloadQueue.StagingFolder, "partial", "9", "x.cbz"), [1], Token);
        await File.WriteAllBytesAsync(Path.Combine(Lib, "half.cbz.part"), [1], Token);
        var folder = (await library.Folders.ListAsync(Token)).Single();

        var first = await scanner.ScanFolderAsync(folder, Token);

        Assert.Equal(3, first.Added);
        var items = (await library.Archives.ListAsync(new(Sort: LibrarySort.Title), Token)).ToDictionary(i => i.Archive.Title);
        Assert.Equal(["Mine", "Other comic", "broken"], items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new GalleryKey("hitomi", "1"), items["Mine"].Archive.Key);
        Assert.Equal(GalleryKey.LocalSource, items["Other comic"].Archive.Key.SourceId);
        Assert.Equal(["2.png", "10.png"], items["Other comic"].Archive.Pages.Select(p => p.File));
        Assert.Equal([102, 110], items["Other comic"].Archive.Pages.Select(p => p.Width));   // measured from headers
        Assert.Equal(ArchiveState.Unreadable, items["broken"].Archive.State);

        var second = await scanner.ScanFolderAsync(folder, Token);
        Assert.Equal((0, 3), (second.Added, second.Unchanged));   // nothing reopened

        File.Delete(saved);
        var third = await scanner.ScanFolderAsync(folder, Token);
        Assert.Equal(1, third.Removed);
    }

    [Fact]
    public async Task Rename_and_move_keep_the_identity()
    {
        var library = await OpenAsync();
        var scanner = new LibraryScanner(library);
        var path = Foreign("Comic.cbz");
        var folder = (await library.Folders.ListAsync(Token)).Single();
        await scanner.ScanFolderAsync(folder, Token);
        var before = (await library.Archives.ListAsync(new(), Token)).Single().Archive.Key;

        Directory.CreateDirectory(Path.Combine(Lib, "moved"));
        File.Move(path, Path.Combine(Lib, "moved", "Renamed.cbz"));
        await scanner.ScanFolderAsync(folder, Token);

        var after = (await library.Archives.ListAsync(new(), Token)).Single();
        Assert.Equal(before, after.Archive.Key);
        Assert.Equal(Path.Combine("moved", "Renamed.cbz"), after.Archive.RelPath);
    }

    [Fact]
    public async Task Missing_folder_marks_galleries_unavailable_until_it_returns()
    {
        var library = await OpenAsync();
        var scanner = new LibraryScanner(library);
        await SavedAsync("Mine", "1");
        var folder = (await library.Folders.ListAsync(Token)).Single();
        await scanner.ScanFolderAsync(folder, Token);

        Directory.Move(Lib, Lib + "-away");
        Assert.False((await scanner.ScanFolderAsync(folder, Token)).Available);
        Assert.Equal(ArchiveState.Unavailable, (await library.Archives.ListAsync(new(), Token)).Single().Archive.State);
        Assert.Null(await library.FindSavedAsync(new GalleryKey("hitomi", "1"), Token));

        Directory.Move(Lib + "-away", Lib);
        await scanner.ScanFolderAsync(folder, Token);
        Assert.Equal(ArchiveState.Ok, (await library.Archives.ListAsync(new(), Token)).Single().Archive.State);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
