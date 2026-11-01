package hentaireader.host.data

import androidx.sqlite.SQLiteConnection
import androidx.sqlite.SQLiteStatement
import androidx.sqlite.driver.bundled.BundledSQLiteDriver
import java.io.File
import java.time.Instant
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/**
 * The local database of Constitution V: library folders, saved archives, history, progress,
 * per-gallery reader settings and downloads in one SQLite file. The same schema as Windows
 * `library.db` (006 data-model) plus `library_folder.display_path` (007 data-model); the version
 * lives in `PRAGMA user_version`. One connection on the bundled SQLite (identical on Android and in
 * JVM tests), used under a lock on the IO dispatcher, so callers never block the UI thread.
 */
class LibraryDatabase private constructor(private val connection: SQLiteConnection) : AutoCloseable {
    private val lock = Mutex()

    /** Runs [block] with exclusive use of the connection, off the caller's thread. */
    suspend fun <T> sql(block: Sql.() -> T): T = lock.withLock { withContext(Dispatchers.IO) { Sql(connection).block() } }

    override fun close() = connection.close()

    companion object {
        const val SCHEMA_VERSION = 1

        /** Opens or creates the database and brings the schema up to date. */
        suspend fun open(file: File): LibraryDatabase = withContext(Dispatchers.IO) {
            file.absoluteFile.parentFile?.mkdirs()
            val connection = BundledSQLiteDriver().open(file.absolutePath)
            val db = LibraryDatabase(connection)
            db.sql {
                exec("PRAGMA journal_mode = WAL")
                exec("PRAGMA foreign_keys = ON")
                val version = scalarLong("PRAGMA user_version")?.toInt() ?: 0
                check(version <= SCHEMA_VERSION) { "library.db has schema $version, this app knows $SCHEMA_VERSION" }
                if (version < 1) transaction {
                    exec(SCHEMA_1)
                    exec("PRAGMA user_version = $SCHEMA_VERSION")
                }
            }
            db
        }

        private val SCHEMA_1 = """
            CREATE TABLE library_folder (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                display_path TEXT NOT NULL DEFAULT '',
                is_target INTEGER NOT NULL DEFAULT 0,
                added_at TEXT NOT NULL
            );
            CREATE TABLE archive (
                id INTEGER PRIMARY KEY,
                folder_id INTEGER NOT NULL REFERENCES library_folder(id) ON DELETE CASCADE,
                rel_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified_at TEXT NOT NULL,
                source_id TEXT NOT NULL,
                gallery_id TEXT NOT NULL,
                title TEXT NOT NULL,
                title_original TEXT,
                language TEXT,
                type TEXT,
                tags_json TEXT NOT NULL DEFAULT '[]',
                page_count INTEGER NOT NULL,
                pages_json TEXT NOT NULL DEFAULT '[]',
                saved_at TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'ok',
                format TEXT NOT NULL,
                metadata_dirty INTEGER NOT NULL DEFAULT 0,
                UNIQUE (folder_id, rel_path)
            );
            CREATE INDEX archive_identity ON archive (source_id, gallery_id);
            CREATE INDEX archive_title ON archive (title COLLATE NOCASE);
            CREATE INDEX archive_saved ON archive (saved_at);
            CREATE TABLE history (
                source_id TEXT NOT NULL,
                gallery_id TEXT NOT NULL,
                title TEXT NOT NULL,
                cover_file TEXT,
                page_count INTEGER NOT NULL,
                last_read_at TEXT NOT NULL,
                PRIMARY KEY (source_id, gallery_id)
            );
            CREATE INDEX history_last_read ON history (last_read_at DESC);
            CREATE TABLE progress (
                source_id TEXT NOT NULL,
                gallery_id TEXT NOT NULL,
                page INTEGER NOT NULL,
                offset REAL NOT NULL,
                page_count INTEGER NOT NULL,
                finished INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (source_id, gallery_id)
            );
            CREATE TABLE reader_settings (
                source_id TEXT NOT NULL,
                gallery_id TEXT NOT NULL,
                zoom REAL NOT NULL,
                fit_width INTEGER NOT NULL,
                gaps INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (source_id, gallery_id)
            );
            CREATE TABLE download (
                id INTEGER PRIMARY KEY,
                source_id TEXT NOT NULL,
                gallery_id TEXT NOT NULL,
                title TEXT NOT NULL,
                gallery_json TEXT NOT NULL,
                settings_json TEXT NOT NULL DEFAULT '{}',
                folder_id INTEGER NOT NULL REFERENCES library_folder(id),
                state TEXT NOT NULL,
                page_count INTEGER NOT NULL,
                pages_done INTEGER NOT NULL DEFAULT 0,
                error_kind TEXT,
                error_message TEXT,
                retry_after TEXT,
                queued_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE download_page (
                download_id INTEGER NOT NULL REFERENCES download(id) ON DELETE CASCADE,
                page INTEGER NOT NULL,
                file TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                PRIMARY KEY (download_id, page)
            );
        """.trimIndent()
    }
}

/**
 * Plain SQL on the connection with the Windows repositories' notation: `$name` parameters (bound by
 * name, a name may repeat) and several statements per command separated by `;`. Values: String,
 * Int, Long, Double, Boolean (0/1), Instant (ISO 8601 UTC) or null.
 */
class Sql internal constructor(private val connection: SQLiteConnection) {
    class Row internal constructor(private val s: SQLiteStatement) {
        fun string(i: Int): String = s.getText(i)
        fun stringOrNull(i: Int): String? = if (s.isNull(i)) null else s.getText(i)
        fun long(i: Int): Long = s.getLong(i)
        fun int(i: Int): Int = s.getLong(i).toInt()
        fun double(i: Int): Double = s.getDouble(i)
        fun bool(i: Int): Boolean = s.getLong(i) != 0L
        fun isNull(i: Int): Boolean = s.isNull(i)
        fun time(i: Int): Instant = Instant.parse(s.getText(i))
        fun timeOrNull(i: Int): Instant? = if (s.isNull(i)) null else Instant.parse(s.getText(i))
    }

    /** Runs every statement; rows are read from the last statement that returns any. */
    fun <T> query(sql: String, vararg args: Pair<String, Any?>, read: (Row) -> T): List<T> {
        var rows = emptyList<T>()
        for (statement in split(sql)) {
            connection.prepare(statement).use { s ->
                bind(s, statement, args)
                val out = ArrayList<T>()
                while (s.step()) out.add(read(Row(s)))
                if (out.isNotEmpty() || s.getColumnCount() > 0) rows = out
            }
        }
        return rows
    }

    fun exec(sql: String, vararg args: Pair<String, Any?>) {
        query(sql, *args) { }
    }

    fun scalarLong(sql: String, vararg args: Pair<String, Any?>): Long? =
        query(sql, *args) { if (it.isNull(0)) null else it.long(0) }.firstOrNull()

    fun scalarString(sql: String, vararg args: Pair<String, Any?>): String? =
        query(sql, *args) { it.stringOrNull(0) }.firstOrNull()

    fun <T> transaction(block: Sql.() -> T): T {
        exec("BEGIN IMMEDIATE")
        try {
            val result = block()
            exec("COMMIT")
            return result
        } catch (e: Throwable) {
            exec("ROLLBACK")
            throw e
        }
    }

    private fun bind(s: SQLiteStatement, sql: String, args: Array<out Pair<String, Any?>>) {
        // SQLite numbers named parameters by first appearance in the statement.
        val names = PARAM.findAll(sql).map { it.value }.distinct().toList()
        val values = args.toMap()
        names.forEachIndexed { i, name ->
            val index = i + 1
            when (val v = values[name]) {
                null -> s.bindNull(index)
                is String -> s.bindText(index, v)
                is Int -> s.bindLong(index, v.toLong())
                is Long -> s.bindLong(index, v)
                is Double -> s.bindDouble(index, v)
                is Boolean -> s.bindLong(index, if (v) 1 else 0)
                is Instant -> s.bindText(index, v.toString())
                else -> error("unsupported SQL value ${v::class}")
            }
        }
    }

    private companion object {
        val PARAM = Regex("\\$[A-Za-z_][A-Za-z0-9_]*")

        // Statements never contain ';' inside literals in this code base.
        fun split(sql: String) = sql.split(';').map { it.trim() }.filter { it.isNotEmpty() }
    }
}

/** Escapes LIKE wildcards for a "contains" match; used with `ESCAPE '\'`. */
fun likeContains(text: String) = "%" + text.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
