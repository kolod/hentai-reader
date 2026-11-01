package hentaireader.host.data

/** Reading position per gallery, shared by online and library reading. */
class ProgressRepository(private val db: LibraryDatabase) {
    suspend fun get(key: GalleryKey): Progress? = db.sql {
        query(
            "SELECT page, offset, page_count, finished, updated_at FROM progress WHERE source_id = \$s AND gallery_id = \$g",
            "\$s" to key.sourceId, "\$g" to key.galleryId,
        ) { read(it, 0) }.firstOrNull()
    }

    /** Saves a position; a gallery once finished stays finished. */
    suspend fun set(key: GalleryKey, progress: Progress) = db.sql {
        exec(
            """
            INSERT INTO progress (source_id, gallery_id, page, offset, page_count, finished, updated_at)
            VALUES (${'$'}s, ${'$'}g, ${'$'}p, ${'$'}o, ${'$'}c, ${'$'}f, ${'$'}u)
            ON CONFLICT (source_id, gallery_id) DO UPDATE SET
                page = excluded.page, offset = excluded.offset, page_count = excluded.page_count,
                finished = MAX(progress.finished, excluded.finished), updated_at = excluded.updated_at
            """,
            "\$s" to key.sourceId, "\$g" to key.galleryId,
            "\$p" to progress.page.coerceIn(0, maxOf(0, progress.pageCount - 1)),
            "\$o" to progress.offset.coerceIn(0.0, 1.0), "\$c" to progress.pageCount,
            "\$f" to progress.finished, "\$u" to progress.updatedAt,
        )
    }

    companion object {
        internal fun read(r: Sql.Row, at: Int) = Progress(r.int(at), r.double(at + 1), r.int(at + 2), r.bool(at + 3), r.time(at + 4))
    }
}
