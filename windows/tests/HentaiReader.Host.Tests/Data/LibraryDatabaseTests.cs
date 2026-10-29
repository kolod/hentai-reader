using HentaiReader.Host.Data;
using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Tests.Data;

/// <summary>A database in a temp folder, removed after the test.</summary>
public sealed class TempDatabase : IAsyncDisposable
{
    public string Folder { get; } = Directory.CreateTempSubdirectory("hr-db-").FullName;
    public LibraryDatabase Db { get; private set; } = null!;

    public static async Task<TempDatabase> CreateAsync()
    {
        var t = new TempDatabase();
        t.Db = await LibraryDatabase.OpenAsync(Path.Combine(t.Folder, "library.db"));
        return t;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

public class LibraryDatabaseTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creates_the_schema_once_and_reopens()
    {
        await using var t = await TempDatabase.CreateAsync();
        await using (var c = await t.Db.ConnectAsync(Token))
        await using (var cmd = c.Command("PRAGMA user_version"))
        {
            Assert.Equal(LibraryDatabase.SchemaVersion, Convert.ToInt32(await cmd.ExecuteScalarAsync(Token)));
        }
        await new FolderRepository(t.Db).AddAsync(Path.Combine(t.Folder, "lib"), DateTimeOffset.UtcNow, Token);

        var again = await LibraryDatabase.OpenAsync(t.Db.Path, Token);
        Assert.Single(await new FolderRepository(again).ListAsync(Token));
    }

    [Fact]
    public async Task Refuses_a_newer_schema()
    {
        await using var t = await TempDatabase.CreateAsync();
        await using (var c = await t.Db.ConnectAsync(Token))
        await using (var cmd = c.Command("PRAGMA user_version = 99"))
        {
            await cmd.ExecuteNonQueryAsync(Token);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => LibraryDatabase.OpenAsync(t.Db.Path, Token));
    }

    [Fact]
    public async Task Imports_progress_json_into_progress_and_history()
    {
        await using var t = await TempDatabase.CreateAsync();
        var file = Path.Combine(t.Folder, "progress.json");
        await File.WriteAllTextAsync(file, """
            {
              "hitomi|123": { "Page": 5, "Offset": 0.25, "PageCount": 20, "Read": false, "UpdatedAt": "2026-10-24T10:00:00+00:00", "Title": "One" },
              "ehentai|e/1/abcdef0123": { "Page": 19, "Offset": 1, "PageCount": 20, "Read": true, "UpdatedAt": "2026-10-24T11:00:00+00:00", "Title": "Two" },
              "broken": { "Page": 1 }
            }
            """, Token);

        Assert.Equal(2, await t.Db.ImportProgressJsonAsync(file, Token));

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".imported"));
        var progress = await new ProgressRepository(t.Db).GetAsync(new GalleryKey("hitomi", "123"), Token);
        Assert.Equal((5, 0.25, 20, false), (progress!.Page, progress.Offset, progress.PageCount, progress.Finished));
        var history = await new HistoryRepository(t.Db).PageAsync(0, 10, Token);
        Assert.Equal(["Two", "One"], history.Select(h => h.Title));
        Assert.True(history[0].Progress!.Finished);
        Assert.Equal(0, await t.Db.ImportProgressJsonAsync(file, Token));   // nothing left to import
    }
}
