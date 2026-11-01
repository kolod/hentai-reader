package hentaireader.host.data

import java.time.Instant
import kotlinx.serialization.Serializable

/** Gallery identity (006 data-model): source + gallery id, or "local" + content id for foreign archives. */
data class GalleryKey(val sourceId: String, val galleryId: String) {
    override fun toString() = "$sourceId|$galleryId"

    companion object {
        const val LOCAL_SOURCE = "local"
    }
}

data class Progress(val page: Int, val offset: Double, val pageCount: Int, val finished: Boolean, val updatedAt: Instant)

/** Reader settings remembered per gallery (006 FR-020). */
data class ReaderSettings(val zoom: Double, val fitWidth: Boolean, val gaps: Boolean, val updatedAt: Instant)

data class HistoryEntry(
    val key: GalleryKey,
    val title: String,
    val coverFile: String?,
    val pageCount: Int,
    val lastReadAt: Instant,
    val progress: Progress?,
    val saved: Boolean,
)

/**
 * A library folder. On Android [path] is the document-tree URI and [displayPath] what Settings
 * shows (007 data-model).
 */
data class LibraryFolder(val id: Long, val path: String, val displayPath: String, val isTarget: Boolean, val addedAt: Instant)

enum class ArchiveState(val sql: String) {
    OK("ok"), UNREADABLE("unreadable"), UNAVAILABLE("unavailable");

    companion object {
        fun parse(s: String) = entries.firstOrNull { it.sql == s } ?: OK
    }
}

@Serializable
data class ArchivePage(val file: String, val width: Int, val height: Int)

@Serializable
data class ArchiveTag(val namespace: String? = null, val name: String)

/** A saved gallery: one archive file in a library folder. */
data class ArchiveRow(
    val id: Long = 0,
    val folderId: Long,
    val relPath: String,
    val size: Long,
    val modifiedAt: Instant,
    val key: GalleryKey,
    val title: String,
    val titleOriginal: String? = null,
    val language: String? = null,
    val type: String? = null,
    val tags: List<ArchiveTag> = emptyList(),
    val pages: List<ArchivePage>,
    val savedAt: Instant,
    val state: ArchiveState = ArchiveState.OK,
    /** `hentai-reader-1` or `foreign`. */
    val format: String,
    val metadataDirty: Boolean = false,
)

/** A library list row: the archive plus its folder and reading progress. */
data class LibraryItem(val archive: ArchiveRow, val folderPath: String, val progress: Progress?)

enum class LibrarySort { ADDED, LAST_READ, TITLE }

enum class ReadFilter { ALL, UNREAD, IN_PROGRESS, FINISHED }

data class LibraryQuery(
    val text: String? = null,
    val sourceId: String? = null,
    val read: ReadFilter = ReadFilter.ALL,
    val sort: LibrarySort = LibrarySort.ADDED,
    val offset: Int = 0,
    val limit: Int = 200,
)

enum class DownloadState(val sql: String) {
    QUEUED("queued"), DOWNLOADING("downloading"), ASSEMBLING("assembling"), PAUSED("paused"),
    LIMITED("limited"), FAILED("failed"), DONE("done");

    companion object {
        fun parse(s: String) = entries.firstOrNull { it.sql == s } ?: FAILED
    }
}

/** Host-side stop reasons in `download.error_kind` (007 data-model); not contract error kinds. */
object DownloadReason {
    const val WAITING_WIFI = "waiting_wifi"
    const val STOPPED_BY_SYSTEM = "stopped_by_system"
}

data class DownloadRow(
    val id: Long = 0,
    val key: GalleryKey,
    val title: String,
    /** The gallery from getGallery at queue time (JSON). */
    val galleryJson: String,
    /** Source setting values at queue time (JSON object). */
    val settingsJson: String = "{}",
    val folderId: Long,
    val state: DownloadState = DownloadState.QUEUED,
    val pageCount: Int,
    val pagesDone: Int = 0,
    val errorKind: String? = null,
    val errorMessage: String? = null,
    val retryAfter: Instant? = null,
    val queuedAt: Instant = Instant.now(),
    val updatedAt: Instant = Instant.now(),
)

data class DownloadPage(val page: Int, val file: String, val width: Int, val height: Int)
