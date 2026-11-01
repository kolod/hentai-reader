package hentaireader.host.data

import java.time.Instant

/** Download queue rows and their staged pages (006 data-model "download"). */
class DownloadRepository(private val db: LibraryDatabase) {
    suspend fun add(row: DownloadRow): Long = db.sql {
        scalarLong(
            """
            INSERT INTO download (source_id, gallery_id, title, gallery_json, settings_json, folder_id, state, page_count, pages_done, queued_at, updated_at)
            VALUES (${'$'}s, ${'$'}g, ${'$'}t, ${'$'}gj, ${'$'}sj, ${'$'}f, ${'$'}st, ${'$'}pc, 0, ${'$'}q, ${'$'}q) RETURNING id
            """,
            "\$s" to row.key.sourceId, "\$g" to row.key.galleryId, "\$t" to row.title, "\$gj" to row.galleryJson,
            "\$sj" to row.settingsJson, "\$f" to row.folderId, "\$st" to row.state.sql, "\$pc" to row.pageCount, "\$q" to row.queuedAt,
        )!!
    }

    suspend fun get(id: Long): DownloadRow? = db.sql { query("SELECT $COLUMNS FROM download WHERE id = \$id", "\$id" to id, read = ::read).firstOrNull() }

    /** All downloads in queue order (oldest first). */
    suspend fun list(): List<DownloadRow> = db.sql { query("SELECT $COLUMNS FROM download ORDER BY queued_at, id", read = ::read) }

    /** The not-finished download of a gallery, if any. */
    suspend fun activeFor(key: GalleryKey): DownloadRow? = db.sql {
        query("SELECT $COLUMNS FROM download WHERE source_id = \$s AND gallery_id = \$g AND state <> 'done' ORDER BY id DESC",
            "\$s" to key.sourceId, "\$g" to key.galleryId, read = ::read).firstOrNull()
    }

    suspend fun setState(
        id: Long,
        state: DownloadState,
        errorKind: String? = null,
        errorMessage: String? = null,
        retryAfter: Instant? = null,
        at: Instant = Instant.now(),
    ) = db.sql {
        exec(
            "UPDATE download SET state = \$st, error_kind = \$k, error_message = \$m, retry_after = \$r, updated_at = \$u WHERE id = \$id",
            "\$st" to state.sql, "\$k" to errorKind, "\$m" to errorMessage, "\$r" to retryAfter, "\$u" to at, "\$id" to id,
        )
    }

    /** Records a staged page and returns the new count of done pages. */
    suspend fun addPage(id: Long, page: DownloadPage): Int = db.sql {
        query(
            """
            INSERT OR REPLACE INTO download_page (download_id, page, file, width, height) VALUES (${'$'}id, ${'$'}p, ${'$'}f, ${'$'}w, ${'$'}h);
            UPDATE download SET pages_done = (SELECT COUNT(*) FROM download_page WHERE download_id = ${'$'}id) WHERE id = ${'$'}id;
            SELECT pages_done FROM download WHERE id = ${'$'}id
            """,
            "\$id" to id, "\$p" to page.page, "\$f" to page.file, "\$w" to page.width, "\$h" to page.height,
        ) { it.int(0) }.first()
    }

    suspend fun pages(id: Long): List<DownloadPage> = db.sql {
        query("SELECT page, file, width, height FROM download_page WHERE download_id = \$id ORDER BY page", "\$id" to id) {
            DownloadPage(it.int(0), it.string(1), it.int(2), it.int(3))
        }
    }

    /** Forgets staged pages whose files are gone (checked on start). */
    suspend fun removePages(id: Long, pages: Iterable<Int>) = db.sql {
        for (page in pages) exec("DELETE FROM download_page WHERE download_id = \$id AND page = \$p", "\$id" to id, "\$p" to page)
        exec("UPDATE download SET pages_done = (SELECT COUNT(*) FROM download_page WHERE download_id = \$id) WHERE id = \$id", "\$id" to id)
    }

    suspend fun delete(id: Long) = db.sql { exec("DELETE FROM download WHERE id = \$id", "\$id" to id) }

    /** Removes finished downloads (the list's "Clear finished"). */
    suspend fun deleteDone() = db.sql { exec("DELETE FROM download WHERE state = 'done'") }

    private companion object {
        const val COLUMNS = "id, source_id, gallery_id, title, gallery_json, settings_json, folder_id, state, page_count, pages_done, " +
            "error_kind, error_message, retry_after, queued_at, updated_at"

        fun read(r: Sql.Row) = DownloadRow(
            id = r.long(0),
            key = GalleryKey(r.string(1), r.string(2)),
            title = r.string(3),
            galleryJson = r.string(4),
            settingsJson = r.string(5),
            folderId = r.long(6),
            state = DownloadState.parse(r.string(7)),
            pageCount = r.int(8),
            pagesDone = r.int(9),
            errorKind = r.stringOrNull(10),
            errorMessage = r.stringOrNull(11),
            retryAfter = r.timeOrNull(12),
            queuedAt = r.time(13),
            updatedAt = r.time(14),
        )
    }
}
