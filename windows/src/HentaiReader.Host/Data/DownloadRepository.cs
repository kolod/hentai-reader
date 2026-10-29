using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Data;

/// <summary>Download queue rows and their staged pages (data-model.md "download").</summary>
public sealed class DownloadRepository(LibraryDatabase db)
{
    private const string Columns = """
        id, source_id, gallery_id, title, gallery_json, settings_json, folder_id, state, page_count, pages_done,
        error_kind, error_message, retry_after, queued_at, updated_at
        """;

    public async Task<long> AddAsync(DownloadRow row, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("""
            INSERT INTO download (source_id, gallery_id, title, gallery_json, settings_json, folder_id, state, page_count, pages_done, queued_at, updated_at)
            VALUES ($s, $g, $t, $gj, $sj, $f, $st, $pc, 0, $q, $q) RETURNING id
            """, ("$s", row.Key.SourceId), ("$g", row.Key.GalleryId), ("$t", row.Title), ("$gj", row.GalleryJson), ("$sj", row.SettingsJson),
            ("$f", row.FolderId), ("$st", Name(row.State)), ("$pc", row.PageCount), ("$q", Sql.Time(row.QueuedAt)));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(token));
    }

    public async Task<DownloadRow?> GetAsync(long id, CancellationToken token = default) =>
        (await QueryAsync($"SELECT {Columns} FROM download WHERE id = $id", token, ("$id", id))).FirstOrDefault();

    /// <summary>All downloads in queue order (oldest first).</summary>
    public Task<IReadOnlyList<DownloadRow>> ListAsync(CancellationToken token = default) =>
        QueryAsync($"SELECT {Columns} FROM download ORDER BY queued_at, id", token);

    /// <summary>The not-finished download of a gallery, if any.</summary>
    public async Task<DownloadRow?> ActiveForAsync(GalleryKey key, CancellationToken token = default) =>
        (await QueryAsync($"SELECT {Columns} FROM download WHERE source_id = $s AND gallery_id = $g AND state <> 'done' ORDER BY id DESC",
            token, ("$s", key.SourceId), ("$g", key.GalleryId))).FirstOrDefault();

    public async Task SetStateAsync(long id, DownloadState state, string? errorKind = null, string? errorMessage = null, DateTimeOffset? retryAfter = null,
        DateTimeOffset? at = null, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("UPDATE download SET state = $st, error_kind = $k, error_message = $m, retry_after = $r, updated_at = $u WHERE id = $id",
            ("$st", Name(state)), ("$k", errorKind), ("$m", errorMessage), ("$r", retryAfter is { } ra ? Sql.Time(ra) : null),
            ("$u", Sql.Time(at ?? DateTimeOffset.UtcNow)), ("$id", id));
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>Records a staged page and the new count of done pages.</summary>
    public async Task<int> AddPageAsync(long id, DownloadPage page, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("""
            INSERT OR REPLACE INTO download_page (download_id, page, file, width, height) VALUES ($id, $p, $f, $w, $h);
            UPDATE download SET pages_done = (SELECT COUNT(*) FROM download_page WHERE download_id = $id) WHERE id = $id;
            SELECT pages_done FROM download WHERE id = $id;
            """, ("$id", id), ("$p", page.Page), ("$f", page.File), ("$w", page.Width), ("$h", page.Height));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    public async Task<IReadOnlyList<DownloadPage>> PagesAsync(long id, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("SELECT page, file, width, height FROM download_page WHERE download_id = $id ORDER BY page", ("$id", id));
        await using var r = await cmd.ExecuteReaderAsync(token);
        var list = new List<DownloadPage>();
        while (await r.ReadAsync(token)) list.Add(new DownloadPage(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    /// <summary>Forgets staged pages whose files are gone (checked on start).</summary>
    public async Task RemovePagesAsync(long id, IEnumerable<int> pages, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        foreach (var page in pages)
        {
            await using var cmd = c.Command("DELETE FROM download_page WHERE download_id = $id AND page = $p", ("$id", id), ("$p", page));
            await cmd.ExecuteNonQueryAsync(token);
        }
        await using var count = c.Command("UPDATE download SET pages_done = (SELECT COUNT(*) FROM download_page WHERE download_id = $id) WHERE id = $id", ("$id", id));
        await count.ExecuteNonQueryAsync(token);
    }

    public async Task DeleteAsync(long id, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("DELETE FROM download WHERE id = $id", ("$id", id));
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>Removes finished downloads (the list's "Clear finished").</summary>
    public async Task DeleteDoneAsync(CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("DELETE FROM download WHERE state = 'done'");
        await cmd.ExecuteNonQueryAsync(token);
    }

    private async Task<IReadOnlyList<DownloadRow>> QueryAsync(string sql, CancellationToken token, params (string, object?)[] args)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(token);
        var list = new List<DownloadRow>();
        while (await r.ReadAsync(token)) list.Add(Read(r));
        return list;
    }

    private static DownloadRow Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Key = new GalleryKey(r.GetString(1), r.GetString(2)),
        Title = r.GetString(3),
        GalleryJson = r.GetString(4),
        SettingsJson = r.GetString(5),
        FolderId = r.GetInt64(6),
        State = Parse(r.GetString(7)),
        PageCount = r.GetInt32(8),
        PagesDone = r.GetInt32(9),
        ErrorKind = r.NullableString(10),
        ErrorMessage = r.NullableString(11),
        RetryAfter = r.IsDBNull(12) ? null : Sql.ParseTime(r.GetString(12)),
        QueuedAt = Sql.ParseTime(r.GetString(13)),
        UpdatedAt = Sql.ParseTime(r.GetString(14)),
    };

    public static string Name(DownloadState s) => s.ToString().ToLowerInvariant();

    private static DownloadState Parse(string s) => Enum.TryParse<DownloadState>(s, ignoreCase: true, out var v) ? v : DownloadState.Failed;
}
