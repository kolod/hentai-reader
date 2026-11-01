package hentaireader.host.library

import hentaireader.host.archive.CbzReader
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.DownloadRepository
import hentaireader.host.data.DownloadState
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.LibraryFolder
import hentaireader.host.platform.DocumentTree
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

/**
 * Library folders (port of the Windows LibraryFolders; 006 US4): target for new saves, removal
 * with Move (galleries copied into another library folder, then deleted here) or Keep files. A folder
 * with unfinished downloads is not removed. Android: the last folder may go (007 FR-028), and a move
 * between document trees is a copy and a delete. When the destination already holds the same
 * gallery with identical pages the incoming copy is dropped; different pages keep both.
 */
class LibraryFolders(
    private val folders: FolderRepository,
    private val archives: ArchiveRepository,
    private val downloads: DownloadRepository,
    private val tree: (LibraryFolder) -> DocumentTree,
) {
    /** Why a folder cannot be removed now ("downloads"), or null. */
    suspend fun cannotRemove(id: Long): String? =
        if (downloads.list().any { it.folderId == id && it.state != DownloadState.DONE }) "downloads" else null

    /** The folder leaves the library; its files stay where they are. */
    suspend fun removeKeepFiles(id: Long) {
        cannotRemove(id)?.let { throw IllegalStateException("folder cannot be removed: $it") }
        folders.remove(id)
    }

    /**
     * Moves every saved gallery of folder [id] into [targetId], then removes the folder. A gallery
     * whose move fails stays where it is and the folder is kept (throws after moving the rest).
     */
    suspend fun moveAndRemove(id: Long, targetId: Long, progress: ((Int, Int) -> Unit)? = null) {
        cannotRemove(id)?.let { throw IllegalStateException("folder cannot be removed: $it") }
        val all = folders.list()
        val from = tree(all.first { it.id == id })
        val to = tree(all.first { it.id == targetId })
        val rows = archives.inFolder(id)
        val failed = mutableListOf<String>()
        rows.forEachIndexed { i, row ->
            try {
                withContext(Dispatchers.IO) {
                    ensureActive()
                    val entry = from.stat(row.relPath) ?: return@withContext
                    val incomingId = runCatching { CbzReader(from.openRead(row.relPath)).use { it.contentId() } }.getOrNull()
                    val duplicate = archives.find(row.key).firstOrNull { it.archive.folderId == targetId }
                    val presentId = duplicate?.let { d -> runCatching { CbzReader(to.openRead(d.archive.relPath)).use { it.contentId() } }.getOrNull() }
                    if (incomingId != null && incomingId == presentId) {
                        from.delete(row.relPath)
                        archives.delete(row.id)
                        return@withContext
                    }
                    val created = to.create(entry.name, "application/octet-stream")
                    try {
                        from.inputStream(row.relPath).use { input -> to.outputStream(created).use { input.copyTo(it, 1 shl 16) } }
                    } catch (e: Exception) {
                        to.delete(created)
                        throw e
                    }
                    if (!from.delete(row.relPath)) throw IOException("cannot delete ${entry.name} after copying")
                    archives.move(row.id, targetId, created)
                    to.stat(created)?.let { archives.setFileStamp(row.id, it.size, it.modifiedAt) }
                }
            } catch (e: Exception) {
                if (e is kotlinx.coroutines.CancellationException) throw e
                failed.add("${row.relPath}: ${e.message}")
            }
            progress?.invoke(i + 1, rows.size)
        }
        if (failed.isNotEmpty()) throw IOException("some galleries were not moved:\n" + failed.joinToString("\n"))
        folders.remove(id)
    }
}
