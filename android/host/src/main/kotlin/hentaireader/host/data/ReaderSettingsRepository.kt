package hentaireader.host.data

/** Zoom, width mode and gaps remembered per gallery (006 FR-020). */
class ReaderSettingsRepository(private val db: LibraryDatabase) {
    suspend fun get(key: GalleryKey): ReaderSettings? = db.sql {
        query(
            "SELECT zoom, fit_width, gaps, updated_at FROM reader_settings WHERE source_id = \$s AND gallery_id = \$g",
            "\$s" to key.sourceId, "\$g" to key.galleryId,
        ) { ReaderSettings(it.double(0), it.bool(1), it.bool(2), it.time(3)) }.firstOrNull()
    }

    suspend fun set(key: GalleryKey, settings: ReaderSettings) = db.sql {
        exec(
            """
            INSERT INTO reader_settings (source_id, gallery_id, zoom, fit_width, gaps, updated_at) VALUES (${'$'}s, ${'$'}g, ${'$'}z, ${'$'}w, ${'$'}p, ${'$'}u)
            ON CONFLICT (source_id, gallery_id) DO UPDATE SET
                zoom = excluded.zoom, fit_width = excluded.fit_width, gaps = excluded.gaps, updated_at = excluded.updated_at
            """,
            "\$s" to key.sourceId, "\$g" to key.galleryId, "\$z" to settings.zoom.coerceIn(0.5, 3.0),
            "\$w" to settings.fitWidth, "\$p" to settings.gaps, "\$u" to settings.updatedAt,
        )
    }
}
