package hentaireader.host.data

import java.time.Instant

/**
 * Library folders; once there is one, exactly one is the target for new saves (006 FR-015).
 * Android difference (007 FR-028): there is no default folder, the table may be empty and the last
 * folder may be removed; the next save then asks for a folder again.
 */
class FolderRepository(private val db: LibraryDatabase) {
    suspend fun list(): List<LibraryFolder> = db.sql { all() }

    /** Adds a folder (a document-tree URI); the first folder becomes the target. Returns the existing row for a known path. */
    suspend fun add(path: String, displayPath: String, at: Instant): LibraryFolder = db.sql {
        exec(
            """
            INSERT INTO library_folder (path, display_path, is_target, added_at)
            VALUES (${'$'}p, ${'$'}d, NOT EXISTS (SELECT 1 FROM library_folder), ${'$'}a)
            ON CONFLICT (path) DO NOTHING
            """,
            "\$p" to path, "\$d" to displayPath, "\$a" to at,
        )
        all().first { it.path == path }
    }

    suspend fun setTarget(id: Long) = db.sql {
        exec("UPDATE library_folder SET is_target = (id = \$id) WHERE EXISTS (SELECT 1 FROM library_folder WHERE id = \$id)", "\$id" to id)
    }

    /** Removes a folder and its archive rows (files are not touched); when the target goes, the oldest remaining folder becomes it. */
    suspend fun remove(id: Long) = db.sql {
        transaction {
            exec(
                """
                DELETE FROM library_folder WHERE id = ${'$'}id;
                UPDATE library_folder SET is_target = 1
                WHERE NOT EXISTS (SELECT 1 FROM library_folder WHERE is_target = 1) AND id = (SELECT MIN(id) FROM library_folder)
                """,
                "\$id" to id,
            )
        }
    }

    private fun Sql.all() = query("SELECT id, path, display_path, is_target, added_at FROM library_folder ORDER BY id") {
        LibraryFolder(it.long(0), it.string(1), it.string(2), it.bool(3), it.time(4))
    }
}
