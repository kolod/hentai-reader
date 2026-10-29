namespace HentaiReader.Host.Data;

/// <summary>Galleries opened in the reader, newest first (FR-018, FR-019).</summary>
public sealed class HistoryRepository(LibraryDatabase db)
{
    /// <summary>Records an opening: creates the entry or moves it to the top; keeps an existing cover.</summary>
    public async Task TouchAsync(GalleryKey key, string title, int pageCount, DateTimeOffset at, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("""
            INSERT INTO history (source_id, gallery_id, title, cover_file, page_count, last_read_at)
            VALUES ($s, $g, $t, NULL, $c, $u)
            ON CONFLICT (source_id, gallery_id) DO UPDATE SET
                title = excluded.title, page_count = excluded.page_count, last_read_at = excluded.last_read_at
            """, ("$s", key.SourceId), ("$g", key.GalleryId), ("$t", title), ("$c", pageCount), ("$u", Sql.Time(at)));
        await cmd.ExecuteNonQueryAsync(token);
    }

    public async Task SetCoverAsync(GalleryKey key, string coverFile, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("UPDATE history SET cover_file = $f WHERE source_id = $s AND gallery_id = $g",
            ("$f", coverFile), ("$s", key.SourceId), ("$g", key.GalleryId));
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>One page of the history with progress and whether a readable saved copy exists.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> PageAsync(int offset, int limit, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("""
            SELECT h.source_id, h.gallery_id, h.title, h.cover_file, h.page_count, h.last_read_at,
                   p.page, p.offset, p.page_count, p.finished, p.updated_at,
                   EXISTS (SELECT 1 FROM archive a WHERE a.source_id = h.source_id AND a.gallery_id = h.gallery_id AND a.state = 'ok')
            FROM history h LEFT JOIN progress p ON p.source_id = h.source_id AND p.gallery_id = h.gallery_id
            ORDER BY h.last_read_at DESC
            LIMIT $limit OFFSET $offset
            """, ("$limit", limit), ("$offset", offset));
        await using var r = await cmd.ExecuteReaderAsync(token);
        var list = new List<HistoryEntry>();
        while (await r.ReadAsync(token))
        {
            list.Add(new HistoryEntry(new GalleryKey(r.GetString(0), r.GetString(1)), r.GetString(2), r.NullableString(3), r.GetInt32(4),
                Sql.ParseTime(r.GetString(5)), r.IsDBNull(6) ? null : ProgressRepository.Read(r, 6), r.GetInt32(11) != 0));
        }
        return list;
    }

    public async Task<HistoryEntry?> GetAsync(GalleryKey key, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("SELECT title, cover_file, page_count, last_read_at FROM history WHERE source_id = $s AND gallery_id = $g",
            ("$s", key.SourceId), ("$g", key.GalleryId));
        await using var r = await cmd.ExecuteReaderAsync(token);
        return await r.ReadAsync(token)
            ? new HistoryEntry(key, r.GetString(0), r.NullableString(1), r.GetInt32(2), Sql.ParseTime(r.GetString(3)), null, false)
            : null;
    }

    public async Task<int> CountAsync(CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("SELECT COUNT(*) FROM history");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    /// <summary>Removes one entry; returns its cover file so the caller can delete it.</summary>
    public async Task<string?> RemoveAsync(GalleryKey key, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("DELETE FROM history WHERE source_id = $s AND gallery_id = $g RETURNING cover_file",
            ("$s", key.SourceId), ("$g", key.GalleryId));
        return await cmd.ExecuteScalarAsync(token) as string;
    }

    /// <summary>Removes all entries; returns their cover files.</summary>
    public async Task<IReadOnlyList<string>> ClearAsync(CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("DELETE FROM history RETURNING cover_file");
        await using var r = await cmd.ExecuteReaderAsync(token);
        var covers = new List<string>();
        while (await r.ReadAsync(token)) if (!r.IsDBNull(0)) covers.Add(r.GetString(0));
        return covers;
    }
}
