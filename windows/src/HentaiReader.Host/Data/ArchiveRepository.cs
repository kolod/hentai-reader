using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Data;

/// <summary>Saved galleries (archives) of all library folders: list, search, filters, sorts (FR-011).</summary>
public sealed class ArchiveRepository(LibraryDatabase db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Columns = """
        a.id, a.folder_id, a.rel_path, a.size, a.modified_at, a.source_id, a.gallery_id, a.title, a.title_original,
        a.language, a.type, a.tags_json, a.pages_json, a.saved_at, a.state, a.format, a.metadata_dirty
        """;

    /// <summary>Inserts or replaces the row of an archive (identified by folder + relative path); returns its id.</summary>
    public async Task<long> UpsertAsync(ArchiveRow row, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("""
            INSERT INTO archive (folder_id, rel_path, size, modified_at, source_id, gallery_id, title, title_original, language, type,
                                 tags_json, page_count, pages_json, saved_at, state, format, metadata_dirty)
            VALUES ($f, $r, $size, $m, $s, $g, $t, $to, $l, $ty, $tags, $pc, $pages, $saved, $state, $format, $dirty)
            ON CONFLICT (folder_id, rel_path) DO UPDATE SET
                size = excluded.size, modified_at = excluded.modified_at, source_id = excluded.source_id, gallery_id = excluded.gallery_id,
                title = excluded.title, title_original = excluded.title_original, language = excluded.language, type = excluded.type,
                tags_json = excluded.tags_json, page_count = excluded.page_count, pages_json = excluded.pages_json,
                saved_at = excluded.saved_at, state = excluded.state, format = excluded.format, metadata_dirty = excluded.metadata_dirty
            RETURNING id
            """,
            ("$f", row.FolderId), ("$r", row.RelPath), ("$size", row.Size), ("$m", Sql.Time(row.ModifiedAt)),
            ("$s", row.Key.SourceId), ("$g", row.Key.GalleryId), ("$t", row.Title), ("$to", row.TitleOriginal),
            ("$l", row.Language), ("$ty", row.Type), ("$tags", JsonSerializer.Serialize(row.Tags, Json)),
            ("$pc", row.Pages.Count), ("$pages", JsonSerializer.Serialize(row.Pages, Json)), ("$saved", Sql.Time(row.SavedAt)),
            ("$state", StateName(row.State)), ("$format", row.Format), ("$dirty", row.MetadataDirty ? 1 : 0));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(token));
    }

    public async Task<ArchiveRow?> GetAsync(long id, CancellationToken token = default) =>
        (await QueryAsync($"SELECT {Columns} FROM archive a WHERE a.id = $id", token, ("$id", id))).FirstOrDefault();

    /// <summary>Rows of one folder (for rescans): relative path → row.</summary>
    public async Task<IReadOnlyList<ArchiveRow>> InFolderAsync(long folderId, CancellationToken token = default) =>
        await QueryAsync($"SELECT {Columns} FROM archive a WHERE a.folder_id = $f", token, ("$f", folderId));

    /// <summary>Saved copies of a gallery, readable ones first.</summary>
    public async Task<IReadOnlyList<LibraryItem>> FindAsync(GalleryKey key, CancellationToken token = default)
    {
        var items = await ListCoreAsync("a.source_id = $s AND a.gallery_id = $g", "a.state = 'ok' DESC, a.saved_at DESC", -1, 0, token,
            ("$s", key.SourceId), ("$g", key.GalleryId));
        return items;
    }

    public Task<IReadOnlyList<LibraryItem>> ListAsync(LibraryQuery q, CancellationToken token = default)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("(a.title LIKE $text ESCAPE '\\' OR a.title_original LIKE $text ESCAPE '\\')");
            args.Add(("$text", Sql.LikeContains(q.Text.Trim())));
        }
        if (q.SourceId is not null)
        {
            where.Add("a.source_id = $source");
            args.Add(("$source", q.SourceId));
        }
        where.Add(q.Read switch
        {
            ReadFilter.Unread => "p.source_id IS NULL",
            ReadFilter.InProgress => "p.source_id IS NOT NULL AND p.finished = 0",
            ReadFilter.Finished => "p.finished = 1",
            _ => "1 = 1",
        });
        var order = q.Sort switch
        {
            LibrarySort.Title => "a.title COLLATE NOCASE, a.saved_at DESC",
            LibrarySort.LastRead => "p.updated_at IS NULL, p.updated_at DESC, a.saved_at DESC",
            _ => "a.saved_at DESC",
        };
        return ListCoreAsync(string.Join(" AND ", where), order, q.Limit, q.Offset, token, [.. args]);
    }

    public async Task SetStateAsync(long id, ArchiveState state, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("UPDATE archive SET state = $st WHERE id = $id", ("$st", StateName(state)), ("$id", id));
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>Marks every archive of a folder unavailable (folder missing) or back to ok.</summary>
    public async Task SetFolderAvailableAsync(long folderId, bool available, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command(available
            ? "UPDATE archive SET state = 'ok' WHERE folder_id = $f AND state = 'unavailable'"
            : "UPDATE archive SET state = 'unavailable' WHERE folder_id = $f AND state = 'ok'", ("$f", folderId));
        await cmd.ExecuteNonQueryAsync(token);
    }

    public async Task SetDirtyAsync(long id, bool dirty, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("UPDATE archive SET metadata_dirty = $d WHERE id = $id", ("$d", dirty ? 1 : 0), ("$id", id));
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>After an in-place metadata update the file's size and time changed; keep the rescan from reopening it.</summary>
    public async Task SetFileStampAsync(long id, long size, DateTimeOffset modifiedAt, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("UPDATE archive SET size = $s, modified_at = $m WHERE id = $id", ("$s", size), ("$m", Sql.Time(modifiedAt)), ("$id", id));
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Points a row at the file's new place. The destination folder's watcher may already have scanned
    /// the moved file in as a new row; that duplicate goes, and this row (id, dirty flag) takes its place.
    /// </summary>
    public async Task MoveAsync(long id, long folderId, string relPath, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var tx = await c.BeginTransactionAsync(token);
        await using var cmd = c.Command("""
            DELETE FROM archive WHERE folder_id = $f AND rel_path = $r AND id <> $id;
            UPDATE archive SET folder_id = $f, rel_path = $r WHERE id = $id;
            """, ("$f", folderId), ("$r", relPath), ("$id", id));
        cmd.Transaction = (SqliteTransaction)tx;
        await cmd.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }

    public async Task<IReadOnlyList<LibraryItem>> DirtyAsync(CancellationToken token = default) =>
        await ListCoreAsync("a.metadata_dirty = 1 AND a.state = 'ok'", "a.id", -1, 0, token);

    public async Task DeleteAsync(long id, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("DELETE FROM archive WHERE id = $id", ("$id", id));
        await cmd.ExecuteNonQueryAsync(token);
    }

    public async Task<int> CountAsync(long? folderId = null, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = folderId is { } f ? c.Command("SELECT COUNT(*) FROM archive WHERE folder_id = $f", ("$f", f)) : c.Command("SELECT COUNT(*) FROM archive");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private async Task<IReadOnlyList<LibraryItem>> ListCoreAsync(string where, string order, int limit, int offset, CancellationToken token, params (string, object?)[] args)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command($"""
            SELECT {Columns}, f.path, p.page, p.offset, p.page_count, p.finished, p.updated_at
            FROM archive a JOIN library_folder f ON f.id = a.folder_id
            LEFT JOIN progress p ON p.source_id = a.source_id AND p.gallery_id = a.gallery_id
            WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $offset
            """, [.. args, ("$limit", limit), ("$offset", offset)]);
        await using var r = await cmd.ExecuteReaderAsync(token);
        var list = new List<LibraryItem>();
        while (await r.ReadAsync(token)) list.Add(new LibraryItem(ReadRow(r), r.GetString(17), r.IsDBNull(18) ? null : ProgressRepository.Read(r, 18)));
        return list;
    }

    private async Task<IReadOnlyList<ArchiveRow>> QueryAsync(string sql, CancellationToken token, params (string, object?)[] args)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command(sql, args);
        await using var r = await cmd.ExecuteReaderAsync(token);
        var list = new List<ArchiveRow>();
        while (await r.ReadAsync(token)) list.Add(ReadRow(r));
        return list;
    }

    private static ArchiveRow ReadRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        FolderId = r.GetInt64(1),
        RelPath = r.GetString(2),
        Size = r.GetInt64(3),
        ModifiedAt = Sql.ParseTime(r.GetString(4)),
        Key = new GalleryKey(r.GetString(5), r.GetString(6)),
        Title = r.GetString(7),
        TitleOriginal = r.NullableString(8),
        Language = r.NullableString(9),
        Type = r.NullableString(10),
        Tags = JsonSerializer.Deserialize<List<ArchiveTag>>(r.GetString(11), Json) ?? [],
        Pages = JsonSerializer.Deserialize<List<ArchivePage>>(r.GetString(12), Json) ?? [],
        SavedAt = Sql.ParseTime(r.GetString(13)),
        State = r.GetString(14) switch { "unreadable" => ArchiveState.Unreadable, "unavailable" => ArchiveState.Unavailable, _ => ArchiveState.Ok },
        Format = r.GetString(15),
        MetadataDirty = r.GetInt32(16) != 0,
    };

    private static string StateName(ArchiveState s) => s switch
    {
        ArchiveState.Unreadable => "unreadable",
        ArchiveState.Unavailable => "unavailable",
        _ => "ok",
    };
}
