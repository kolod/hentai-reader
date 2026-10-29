namespace HentaiReader.Host.Data;

/// <summary>Reading position per gallery, shared by online and library reading (replaces progress.json).</summary>
public sealed class ProgressRepository(LibraryDatabase db)
{
    public async Task<Progress?> GetAsync(GalleryKey key, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("SELECT page, offset, page_count, finished, updated_at FROM progress WHERE source_id = $s AND gallery_id = $g",
            ("$s", key.SourceId), ("$g", key.GalleryId));
        await using var r = await cmd.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? Read(r, 0) : null;
    }

    /// <summary>Saves a position; a gallery once finished stays finished.</summary>
    public async Task SetAsync(GalleryKey key, Progress progress, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("""
            INSERT INTO progress (source_id, gallery_id, page, offset, page_count, finished, updated_at)
            VALUES ($s, $g, $p, $o, $c, $f, $u)
            ON CONFLICT (source_id, gallery_id) DO UPDATE SET
                page = excluded.page, offset = excluded.offset, page_count = excluded.page_count,
                finished = MAX(progress.finished, excluded.finished), updated_at = excluded.updated_at
            """,
            ("$s", key.SourceId), ("$g", key.GalleryId),
            ("$p", Math.Clamp(progress.Page, 0, Math.Max(0, progress.PageCount - 1))),
            ("$o", Math.Clamp(progress.Offset, 0, 1)), ("$c", progress.PageCount),
            ("$f", progress.Finished ? 1 : 0), ("$u", Sql.Time(progress.UpdatedAt)));
        await cmd.ExecuteNonQueryAsync(token);
    }

    internal static Progress Read(Microsoft.Data.Sqlite.SqliteDataReader r, int at) =>
        new(r.GetInt32(at), r.GetDouble(at + 1), r.GetInt32(at + 2), r.GetInt32(at + 3) != 0, Sql.ParseTime(r.GetString(at + 4)));
}
