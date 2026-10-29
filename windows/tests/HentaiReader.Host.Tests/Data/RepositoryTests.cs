using System.Diagnostics;
using HentaiReader.Host.Data;

namespace HentaiReader.Host.Tests.Data;

public class RepositoryTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-25T08:00:00Z");

    [Fact]
    public async Task Progress_keeps_finished_and_clamps()
    {
        await using var t = await TempDatabase.CreateAsync();
        var repo = new ProgressRepository(t.Db);
        var key = new GalleryKey("hitomi", "1");
        await repo.SetAsync(key, new Progress(9, 1, 10, true, T0), Token);
        await repo.SetAsync(key, new Progress(50, 2, 10, false, T0.AddMinutes(1)), Token);

        var p = await repo.GetAsync(key, Token);
        Assert.Equal((9, 1.0, true), (p!.Page, p.Offset, p.Finished));
    }

    [Fact]
    public async Task Exactly_one_target_folder_and_the_last_cannot_go()
    {
        await using var t = await TempDatabase.CreateAsync();
        var repo = new FolderRepository(t.Db);
        var a = await repo.AddAsync(Path.Combine(t.Folder, "a"), T0, Token);
        var b = await repo.AddAsync(Path.Combine(t.Folder, "b") + Path.DirectorySeparatorChar, T0, Token);
        Assert.True(a.IsTarget);
        Assert.False(b.IsTarget);
        Assert.Equal(b.Id, (await repo.AddAsync(Path.Combine(t.Folder, "B"), T0, Token)).Id);   // same path, case-insensitive

        await repo.SetTargetAsync(b.Id, Token);
        Assert.Equal([b.Id], (await repo.ListAsync(Token)).Where(f => f.IsTarget).Select(f => f.Id));

        await repo.RemoveAsync(b.Id, Token);
        Assert.True((await repo.ListAsync(Token)).Single().IsTarget);   // a took over
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RemoveAsync(a.Id, Token));
    }

    [Fact]
    public async Task History_is_newest_first_with_progress_and_saved_mark()
    {
        await using var t = await TempDatabase.CreateAsync();
        var history = new HistoryRepository(t.Db);
        await history.TouchAsync(new("hitomi", "1"), "Old", 10, T0, Token);
        await history.TouchAsync(new("hitomi", "2"), "New", 20, T0.AddHours(1), Token);
        await history.TouchAsync(new("hitomi", "1"), "Old again", 10, T0.AddHours(2), Token);
        await new ProgressRepository(t.Db).SetAsync(new("hitomi", "2"), new Progress(3, 0, 20, false, T0), Token);
        var folder = await new FolderRepository(t.Db).AddAsync(t.Folder, T0, Token);
        await new ArchiveRepository(t.Db).UpsertAsync(Archive(folder.Id, "2.cbz", new("hitomi", "2"), "New", T0), Token);

        var page = await history.PageAsync(0, 10, Token);

        Assert.Equal(["Old again", "New"], page.Select(e => e.Title));
        Assert.Equal(3, page[1].Progress!.Page);
        Assert.Equal([false, true], page.Select(e => e.Saved));
        Assert.Null(await history.RemoveAsync(new("hitomi", "1"), Token));
        Assert.Equal(1, await history.CountAsync(Token));
        await history.ClearAsync(Token);
        Assert.Equal(0, await history.CountAsync(Token));
    }

    [Fact]
    public async Task History_page_of_5000_entries_is_fast()
    {
        await using var t = await TempDatabase.CreateAsync();
        await using (var c = await t.Db.ConnectAsync(Token))
        await using (var tx = await c.BeginTransactionAsync(Token))
        {
            for (var i = 0; i < 5000; i++)
            {
                await using var cmd = c.Command("INSERT INTO history VALUES ('hitomi', $g, $t, NULL, 30, $u)",
                    ("$g", i.ToString()), ("$t", $"Gallery {i}"), ("$u", Sql.Time(T0.AddSeconds(i))));
                await cmd.ExecuteNonQueryAsync(Token);
            }
            await tx.CommitAsync(Token);
        }
        var sw = Stopwatch.StartNew();
        var page = await new HistoryRepository(t.Db).PageAsync(0, 100, Token);
        Assert.Equal("Gallery 4999", page[0].Title);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Library_search_filters_and_sorts_combine()
    {
        await using var t = await TempDatabase.CreateAsync();
        var folder = await new FolderRepository(t.Db).AddAsync(t.Folder, T0, Token);
        var archives = new ArchiveRepository(t.Db);
        await archives.UpsertAsync(Archive(folder.Id, "a.cbz", new("hitomi", "1"), "Alpha glasses", T0), Token);
        await archives.UpsertAsync(Archive(folder.Id, "b.cbz", new("ehentai", "e/2/abcdef0123"), "Beta", T0.AddDays(1)), Token);
        await archives.UpsertAsync(Archive(folder.Id, "c.cbz", new("hitomi", "3"), "Gamma 100%_off", T0.AddDays(2)), Token);
        var progress = new ProgressRepository(t.Db);
        await progress.SetAsync(new("hitomi", "1"), new Progress(9, 1, 10, true, T0.AddDays(5)), Token);
        await progress.SetAsync(new("hitomi", "3"), new Progress(2, 0, 10, false, T0.AddDays(4)), Token);

        async Task<string[]> Titles(LibraryQuery q) => (await archives.ListAsync(q, Token)).Select(i => i.Archive.Title).ToArray();

        Assert.Equal(["Gamma 100%_off", "Beta", "Alpha glasses"], await Titles(new()));
        Assert.Equal(["Alpha glasses", "Beta", "Gamma 100%_off"], await Titles(new(Sort: LibrarySort.Title)));
        Assert.Equal(["Alpha glasses", "Gamma 100%_off", "Beta"], await Titles(new(Sort: LibrarySort.LastRead)));
        Assert.Equal(["Alpha glasses"], await Titles(new(Text: "GLASS")));
        Assert.Equal(["Gamma 100%_off"], await Titles(new(Text: "%_")));   // wildcards are literal
        Assert.Equal(["Beta"], await Titles(new(Read: ReadFilter.Unread)));
        Assert.Equal(["Gamma 100%_off"], await Titles(new(SourceId: "hitomi", Read: ReadFilter.InProgress)));
        Assert.Equal(["Alpha glasses"], await Titles(new(Read: ReadFilter.Finished)));

        var found = await archives.FindAsync(new("hitomi", "3"), Token);
        Assert.Equal(Path.Combine(t.Folder, "c.cbz"), found.Single().FullPath);
        await archives.SetFolderAvailableAsync(folder.Id, available: false, Token);
        Assert.All(await archives.ListAsync(new(), Token), i => Assert.Equal(ArchiveState.Unavailable, i.Archive.State));
    }

    [Fact]
    public async Task Downloads_track_pages_and_states()
    {
        await using var t = await TempDatabase.CreateAsync();
        var folder = await new FolderRepository(t.Db).AddAsync(t.Folder, T0, Token);
        var repo = new DownloadRepository(t.Db);
        var id = await repo.AddAsync(new DownloadRow { Key = new("hitomi", "1"), Title = "G", GalleryJson = "{}", FolderId = folder.Id, PageCount = 3, QueuedAt = T0 }, Token);

        Assert.Equal(1, await repo.AddPageAsync(id, new DownloadPage(0, "0.webp", 100, 150), Token));
        Assert.Equal(2, await repo.AddPageAsync(id, new DownloadPage(2, "2.webp", 100, 150), Token));
        Assert.Equal(2, await repo.AddPageAsync(id, new DownloadPage(2, "2.webp", 100, 150), Token));   // same page again
        await repo.SetStateAsync(id, DownloadState.Limited, "RateLimited", "limit", T0.AddMinutes(5), T0, Token);

        var row = await repo.GetAsync(id, Token);
        Assert.Equal((DownloadState.Limited, 2, "RateLimited"), (row!.State, row.PagesDone, row.ErrorKind));
        Assert.Equal(id, (await repo.ActiveForAsync(new("hitomi", "1"), Token))!.Id);
        await repo.RemovePagesAsync(id, [2], Token);
        Assert.Equal([0], (await repo.PagesAsync(id, Token)).Select(p => p.Page));
        await repo.DeleteAsync(id, Token);
        Assert.Empty(await repo.ListAsync(Token));
    }

    internal static ArchiveRow Archive(long folderId, string rel, GalleryKey key, string title, DateTimeOffset saved) => new()
    {
        FolderId = folderId,
        RelPath = rel,
        Size = 100,
        ModifiedAt = saved,
        Key = key,
        Title = title,
        Pages = [new ArchivePage("0001.webp", 100, 150)],
        SavedAt = saved,
        Format = "hentai-reader-1",
    };
}
