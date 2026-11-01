package hentaireader.host.data

import java.time.Instant

/** Galleries opened in the reader, newest first (006 FR-018, FR-019). */
class HistoryRepository(private val db: LibraryDatabase) {
    /** Records an opening: creates the entry or moves it to the top; keeps an existing cover. */
    suspend fun touch(key: GalleryKey, title: String, pageCount: Int, at: Instant) = db.sql {
        exec(
            """
            INSERT INTO history (source_id, gallery_id, title, cover_file, page_count, last_read_at)
            VALUES (${'$'}s, ${'$'}g, ${'$'}t, NULL, ${'$'}c, ${'$'}u)
            ON CONFLICT (source_id, gallery_id) DO UPDATE SET
                title = excluded.title, page_count = excluded.page_count, last_read_at = excluded.last_read_at
            """,
            "\$s" to key.sourceId, "\$g" to key.galleryId, "\$t" to title, "\$c" to pageCount, "\$u" to at,
        )
    }

    suspend fun setCover(key: GalleryKey, coverFile: String) = db.sql {
        exec("UPDATE history SET cover_file = \$f WHERE source_id = \$s AND gallery_id = \$g",
            "\$f" to coverFile, "\$s" to key.sourceId, "\$g" to key.galleryId)
    }

    /** One page of the history with progress and whether a readable saved copy exists. */
    suspend fun page(offset: Int, limit: Int): List<HistoryEntry> = db.sql {
        query(
            """
            SELECT h.source_id, h.gallery_id, h.title, h.cover_file, h.page_count, h.last_read_at,
                   p.page, p.offset, p.page_count, p.finished, p.updated_at,
                   EXISTS (SELECT 1 FROM archive a WHERE a.source_id = h.source_id AND a.gallery_id = h.gallery_id AND a.state = 'ok')
            FROM history h LEFT JOIN progress p ON p.source_id = h.source_id AND p.gallery_id = h.gallery_id
            ORDER BY h.last_read_at DESC
            LIMIT ${'$'}limit OFFSET ${'$'}offset
            """,
            "\$limit" to limit, "\$offset" to offset,
        ) {
            HistoryEntry(GalleryKey(it.string(0), it.string(1)), it.string(2), it.stringOrNull(3), it.int(4), it.time(5),
                if (it.isNull(6)) null else ProgressRepository.read(it, 6), it.bool(11))
        }
    }

    suspend fun get(key: GalleryKey): HistoryEntry? = db.sql {
        query("SELECT title, cover_file, page_count, last_read_at FROM history WHERE source_id = \$s AND gallery_id = \$g",
            "\$s" to key.sourceId, "\$g" to key.galleryId) {
            HistoryEntry(key, it.string(0), it.stringOrNull(1), it.int(2), it.time(3), null, false)
        }.firstOrNull()
    }

    suspend fun count(): Int = db.sql { scalarLong("SELECT COUNT(*) FROM history")!!.toInt() }

    /** Removes one entry; returns its cover file so the caller can delete it. */
    suspend fun remove(key: GalleryKey): String? = db.sql {
        query("DELETE FROM history WHERE source_id = \$s AND gallery_id = \$g RETURNING cover_file",
            "\$s" to key.sourceId, "\$g" to key.galleryId) { it.stringOrNull(0) }.firstOrNull()
    }

    /** Removes all entries; returns their cover files. */
    suspend fun clear(): List<String> = db.sql {
        query("DELETE FROM history RETURNING cover_file") { it.stringOrNull(0) }.filterNotNull()
    }
}
