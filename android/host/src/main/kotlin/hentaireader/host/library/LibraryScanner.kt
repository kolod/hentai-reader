package hentaireader.host.library

import hentaireader.host.archive.ArchiveMetadata
import hentaireader.host.archive.CbzReader
import hentaireader.host.archive.ImageFormat
import hentaireader.host.data.ArchivePage
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveRow
import hentaireader.host.data.ArchiveState
import hentaireader.host.data.ArchiveTag
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.LibraryFolder
import hentaireader.host.platform.DocumentEntry
import hentaireader.host.platform.DocumentTree
import kotlin.math.abs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/**
 * Finds saved galleries in the library folders (port of the Windows LibraryScanner; 006 US3,
 * research R7): archives with this app's metadata are listed with it; other comic archives by file
 * name, with page sizes measured once from the image headers; damaged files as unreadable; an
 * unavailable folder (card removed, access revoked) marks its galleries unavailable. Unchanged files
 * (same size and time) are not reopened. Runs on start, when the Library opens and on pull-to-refresh
 * (007 FR-029): document trees have no change notifications.
 */
class LibraryScanner(
    private val folders: FolderRepository,
    private val archives: ArchiveRepository,
    private val tree: (LibraryFolder) -> DocumentTree,
    /** Measures a page the header reader does not understand (AVIF): entry bytes → size. */
    private val measure: ((ByteArray) -> Pair<Int, Int>?)? = null,
) {
    private val gate = Mutex()

    data class Result(val added: Int, val updated: Int, val removed: Int, val unchanged: Int, val available: Boolean)

    suspend fun scanAll() {
        for (folder in folders.list()) scanFolder(folder)
    }

    suspend fun scanFolder(folder: LibraryFolder): Result = gate.withLock {
        val t = tree(folder)
        val files = withContext(Dispatchers.IO) { if (t.isAvailable()) runCatching { t.listFiles() }.getOrNull() else null }
        if (files == null) {
            archives.setFolderAvailable(folder.id, available = false)
            return Result(0, 0, 0, 0, available = false)
        }
        archives.setFolderAvailable(folder.id, available = true)
        val known = archives.inFolder(folder.id).associateBy { it.relPath }
        val seen = HashSet<String>()
        var added = 0
        var updated = 0
        var unchanged = 0
        for (file in files.filter(::isArchive)) {
            seen.add(file.relPath)
            val row = known[file.relPath]
            if (row != null && row.size == file.size && abs(row.modifiedAt.epochSecond - file.modifiedAt.epochSecond) < 2) {
                unchanged++
                continue
            }
            val fresh = withContext(Dispatchers.IO) { read(folder, t, file) }
            archives.upsert(fresh.copy(metadataDirty = row?.metadataDirty ?: false))
            if (row == null) added++ else updated++
        }
        var removed = 0
        for (row in known.values.filter { it.relPath !in seen }) {
            archives.delete(row.id)
            removed++
        }
        Result(added, updated, removed, unchanged, available = true)
    }

    private fun read(folder: LibraryFolder, t: DocumentTree, file: DocumentEntry): ArchiveRow {
        val title = file.name.substringBeforeLast('.')
        return try {
            CbzReader(t.openRead(file.relPath)).use { reader ->
                reader.readMetadata()?.let { return rowFor(folder.id, file, it) }
                if (reader.pages.isEmpty()) throw IllegalStateException("no page images")
                val pages = reader.pages.map { entry ->
                    var size = ImageFormat.size(reader.readEntryPrefix(entry, HEADER_BYTES))
                    if (size == null) size = measure?.invoke(reader.readEntry(entry))
                    ArchivePage(entry.name, size?.first ?: 0, size?.second ?: 0)
                }
                ArchiveRow(
                    folderId = folder.id, relPath = file.relPath, size = file.size, modifiedAt = file.modifiedAt,
                    key = GalleryKey(GalleryKey.LOCAL_SOURCE, reader.contentId()), title = title, pages = pages,
                    savedAt = file.modifiedAt, format = "foreign",
                )
            }
        } catch (e: Exception) {
            ArchiveRow(
                folderId = folder.id, relPath = file.relPath, size = file.size, modifiedAt = file.modifiedAt,
                key = GalleryKey(GalleryKey.LOCAL_SOURCE, "unreadable:${file.relPath}"), title = title, pages = emptyList(),
                savedAt = file.modifiedAt, state = ArchiveState.UNREADABLE, format = "foreign",
            )
        }
    }

    companion object {
        private const val HEADER_BYTES = 64 * 1024

        /** Comic archives; half-written ones (`.cbz.partial`, `.rewrite`) are never listed. */
        fun isArchive(file: DocumentEntry): Boolean {
            val name = file.name.lowercase()
            return !file.isDirectory && (name.endsWith(".cbz") || name.endsWith(".zip"))
        }

        /** The archive row for an archive with this app's metadata. */
        fun rowFor(folderId: Long, file: DocumentEntry, m: ArchiveMetadata) = ArchiveRow(
            folderId = folderId, relPath = file.relPath, size = file.size, modifiedAt = file.modifiedAt,
            key = GalleryKey(m.source.id, m.source.galleryId), title = m.title, titleOriginal = m.titleOriginal,
            language = m.language, type = m.type, tags = m.tags.map { ArchiveTag(it.namespace, it.name) },
            pages = m.pages.map { ArchivePage(it.file, it.width, it.height) }, savedAt = m.savedAt, format = "hentai-reader-1",
        )
    }
}
