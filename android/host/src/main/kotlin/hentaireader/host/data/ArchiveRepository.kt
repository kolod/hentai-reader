package hentaireader.host.data

import java.time.Instant
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json

/** Saved galleries (archives) of all library folders: list, search, filters, sorts (006 FR-011). */
class ArchiveRepository(private val db: LibraryDatabase) {
    /** Inserts or replaces the row of an archive (identified by folder + relative path); returns its id. */
    suspend fun upsert(row: ArchiveRow): Long = db.sql {
        scalarLong(
            """
            INSERT INTO archive (folder_id, rel_path, size, modified_at, source_id, gallery_id, title, title_original, language, type,
                                 tags_json, page_count, pages_json, saved_at, state, format, metadata_dirty)
            VALUES (${'$'}f, ${'$'}r, ${'$'}size, ${'$'}m, ${'$'}s, ${'$'}g, ${'$'}t, ${'$'}to, ${'$'}l, ${'$'}ty, ${'$'}tags, ${'$'}pc, ${'$'}pages, ${'$'}saved, ${'$'}state, ${'$'}format, ${'$'}dirty)
            ON CONFLICT (folder_id, rel_path) DO UPDATE SET
                size = excluded.size, modified_at = excluded.modified_at, source_id = excluded.source_id, gallery_id = excluded.gallery_id,
                title = excluded.title, title_original = excluded.title_original, language = excluded.language, type = excluded.type,
                tags_json = excluded.tags_json, page_count = excluded.page_count, pages_json = excluded.pages_json,
                saved_at = excluded.saved_at, state = excluded.state, format = excluded.format, metadata_dirty = excluded.metadata_dirty
            RETURNING id
            """,
            "\$f" to row.folderId, "\$r" to row.relPath, "\$size" to row.size, "\$m" to row.modifiedAt,
            "\$s" to row.key.sourceId, "\$g" to row.key.galleryId, "\$t" to row.title, "\$to" to row.titleOriginal,
            "\$l" to row.language, "\$ty" to row.type, "\$tags" to Json.encodeToString(TAGS, row.tags),
            "\$pc" to row.pages.size, "\$pages" to Json.encodeToString(PAGES, row.pages), "\$saved" to row.savedAt,
            "\$state" to row.state.sql, "\$format" to row.format, "\$dirty" to row.metadataDirty,
        )!!
    }

    suspend fun get(id: Long): ArchiveRow? = db.sql { query("SELECT $COLUMNS FROM archive a WHERE a.id = \$id", "\$id" to id, read = ::readRow).firstOrNull() }

    /** Rows of one folder (for rescans). */
    suspend fun inFolder(folderId: Long): List<ArchiveRow> = db.sql { query("SELECT $COLUMNS FROM archive a WHERE a.folder_id = \$f", "\$f" to folderId, read = ::readRow) }

    /** Saved copies of a gallery, readable ones first. */
    suspend fun find(key: GalleryKey): List<LibraryItem> = db.sql {
        listCore("a.source_id = \$s AND a.gallery_id = \$g", "a.state = 'ok' DESC, a.saved_at DESC", -1, 0,
            "\$s" to key.sourceId, "\$g" to key.galleryId)
    }

    suspend fun list(q: LibraryQuery): List<LibraryItem> = db.sql {
        val where = mutableListOf<String>()
        val args = mutableListOf<Pair<String, Any?>>()
        if (!q.text.isNullOrBlank()) {
            where.add("(a.title LIKE \$text ESCAPE '\\' OR a.title_original LIKE \$text ESCAPE '\\')")
            args.add("\$text" to likeContains(q.text.trim()))
        }
        if (q.sourceId != null) {
            where.add("a.source_id = \$source")
            args.add("\$source" to q.sourceId)
        }
        where.add(when (q.read) {
            ReadFilter.UNREAD -> "p.source_id IS NULL"
            ReadFilter.IN_PROGRESS -> "p.source_id IS NOT NULL AND p.finished = 0"
            ReadFilter.FINISHED -> "p.finished = 1"
            ReadFilter.ALL -> "1 = 1"
        })
        val order = when (q.sort) {
            LibrarySort.TITLE -> "a.title COLLATE NOCASE, a.saved_at DESC"
            LibrarySort.LAST_READ -> "p.updated_at IS NULL, p.updated_at DESC, a.saved_at DESC"
            LibrarySort.ADDED -> "a.saved_at DESC"
        }
        listCore(where.joinToString(" AND "), order, q.limit, q.offset, *args.toTypedArray())
    }

    suspend fun setState(id: Long, state: ArchiveState) = db.sql { exec("UPDATE archive SET state = \$st WHERE id = \$id", "\$st" to state.sql, "\$id" to id) }

    /** Marks every archive of a folder unavailable (folder missing or no access) or back to ok. */
    suspend fun setFolderAvailable(folderId: Long, available: Boolean) = db.sql {
        exec(
            if (available) "UPDATE archive SET state = 'ok' WHERE folder_id = \$f AND state = 'unavailable'"
            else "UPDATE archive SET state = 'unavailable' WHERE folder_id = \$f AND state = 'ok'",
            "\$f" to folderId,
        )
    }

    suspend fun setDirty(id: Long, dirty: Boolean) = db.sql { exec("UPDATE archive SET metadata_dirty = \$d WHERE id = \$id", "\$d" to dirty, "\$id" to id) }

    /** After an in-place metadata update the file's size and time changed; keep the rescan from reopening it. */
    suspend fun setFileStamp(id: Long, size: Long, modifiedAt: Instant) = db.sql {
        exec("UPDATE archive SET size = \$s, modified_at = \$m WHERE id = \$id", "\$s" to size, "\$m" to modifiedAt, "\$id" to id)
    }

    /** Points a row at the file's new place; a duplicate row a rescan made for the moved file goes. */
    suspend fun move(id: Long, folderId: Long, relPath: String) = db.sql {
        transaction {
            exec(
                """
                DELETE FROM archive WHERE folder_id = ${'$'}f AND rel_path = ${'$'}r AND id <> ${'$'}id;
                UPDATE archive SET folder_id = ${'$'}f, rel_path = ${'$'}r WHERE id = ${'$'}id
                """,
                "\$f" to folderId, "\$r" to relPath, "\$id" to id,
            )
        }
    }

    suspend fun dirty(): List<LibraryItem> = db.sql { listCore("a.metadata_dirty = 1 AND a.state = 'ok'", "a.id", -1, 0) }

    suspend fun delete(id: Long) = db.sql { exec("DELETE FROM archive WHERE id = \$id", "\$id" to id) }

    suspend fun count(folderId: Long? = null): Int = db.sql {
        (if (folderId != null) scalarLong("SELECT COUNT(*) FROM archive WHERE folder_id = \$f", "\$f" to folderId)
        else scalarLong("SELECT COUNT(*) FROM archive"))!!.toInt()
    }

    private fun Sql.listCore(where: String, order: String, limit: Int, offset: Int, vararg args: Pair<String, Any?>): List<LibraryItem> =
        query(
            """
            SELECT $COLUMNS, f.path, p.page, p.offset, p.page_count, p.finished, p.updated_at
            FROM archive a JOIN library_folder f ON f.id = a.folder_id
            LEFT JOIN progress p ON p.source_id = a.source_id AND p.gallery_id = a.gallery_id
            WHERE $where ORDER BY $order LIMIT ${'$'}limit OFFSET ${'$'}offset
            """,
            *args, "\$limit" to limit, "\$offset" to offset,
        ) { LibraryItem(readRow(it), it.string(17), if (it.isNull(18)) null else ProgressRepository.read(it, 18)) }

    private companion object {
        const val COLUMNS = "a.id, a.folder_id, a.rel_path, a.size, a.modified_at, a.source_id, a.gallery_id, a.title, a.title_original, " +
            "a.language, a.type, a.tags_json, a.pages_json, a.saved_at, a.state, a.format, a.metadata_dirty"
        val TAGS = ListSerializer(ArchiveTag.serializer())
        val PAGES = ListSerializer(ArchivePage.serializer())
        val LENIENT = Json { ignoreUnknownKeys = true }

        fun readRow(r: Sql.Row) = ArchiveRow(
            id = r.long(0),
            folderId = r.long(1),
            relPath = r.string(2),
            size = r.long(3),
            modifiedAt = r.time(4),
            key = GalleryKey(r.string(5), r.string(6)),
            title = r.string(7),
            titleOriginal = r.stringOrNull(8),
            language = r.stringOrNull(9),
            type = r.stringOrNull(10),
            tags = LENIENT.decodeFromString(TAGS, r.string(11)),
            pages = LENIENT.decodeFromString(PAGES, r.string(12)),
            savedAt = r.time(13),
            state = ArchiveState.parse(r.string(14)),
            format = r.string(15),
            metadataDirty = r.bool(16),
        )
    }
}
