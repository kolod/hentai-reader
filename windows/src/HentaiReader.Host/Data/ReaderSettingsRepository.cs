namespace HentaiReader.Host.Data;

/// <summary>Zoom, width mode and gaps remembered per gallery (FR-020).</summary>
public sealed class ReaderSettingsRepository(LibraryDatabase db)
{
    public async Task<ReaderSettings?> GetAsync(GalleryKey key, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("SELECT zoom, fit_width, gaps, updated_at FROM reader_settings WHERE source_id = $s AND gallery_id = $g",
            ("$s", key.SourceId), ("$g", key.GalleryId));
        await using var r = await cmd.ExecuteReaderAsync(token);
        return await r.ReadAsync(token)
            ? new ReaderSettings(r.GetDouble(0), r.GetInt32(1) != 0, r.GetInt32(2) != 0, Sql.ParseTime(r.GetString(3)))
            : null;
    }

    public async Task SetAsync(GalleryKey key, ReaderSettings settings, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("""
            INSERT INTO reader_settings (source_id, gallery_id, zoom, fit_width, gaps, updated_at) VALUES ($s, $g, $z, $w, $p, $u)
            ON CONFLICT (source_id, gallery_id) DO UPDATE SET
                zoom = excluded.zoom, fit_width = excluded.fit_width, gaps = excluded.gaps, updated_at = excluded.updated_at
            """, ("$s", key.SourceId), ("$g", key.GalleryId), ("$z", Math.Clamp(settings.Zoom, 0.5, 3)),
            ("$w", settings.FitWidth ? 1 : 0), ("$p", settings.Gaps ? 1 : 0), ("$u", Sql.Time(settings.UpdatedAt)));
        await cmd.ExecuteNonQueryAsync(token);
    }
}
