package hentaireader.host.downloads

import hentaireader.host.archive.ArchiveMetadata
import hentaireader.host.archive.CbzWriter
import hentaireader.host.archive.ImageFormat
import hentaireader.host.data.ArchivePage
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveRow
import hentaireader.host.data.ArchiveTag
import hentaireader.host.data.DownloadPage
import hentaireader.host.data.DownloadReason
import hentaireader.host.data.DownloadRepository
import hentaireader.host.data.DownloadRow
import hentaireader.host.data.DownloadState
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.LibraryFolder
import hentaireader.host.data.ProgressRepository
import hentaireader.host.data.ReaderSettingsRepository
import hentaireader.host.engine.SourceErrorKind
import hentaireader.host.http.ImageHttpException
import hentaireader.host.platform.Clock
import hentaireader.host.platform.DocumentTree
import hentaireader.host.reading.PageAttempts
import hentaireader.host.reading.PageImageLoader
import hentaireader.host.reading.PageResolveException
import hentaireader.host.reading.PageTimeoutException
import hentaireader.host.sources.SessionState
import hentaireader.host.sources.SourceLimitedException
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.PageCache
import java.io.File
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/** A source the queue can download from: its running session, display name and current settings. */
class DownloadSource(val session: SourceSession, val name: String, val settings: Map<String, String>?)

/**
 * Saves galleries into the library (port of the Windows DownloadQueue; 006 research R5, 007
 * FR-026): one worker per source, every page through the source as online reading does (page cache
 * first, else getPageUrl + download with retry hints and the stall timeout), staged in app storage,
 * then written as one archive into the target library folder (`.cbz.partial`, renamed when complete).
 * Rate limits and sign-in requirements stop the download and keep its pages (Constitution VI); a
 * restart continues where it stopped. Android additions: [blockedReason] (Wi-Fi only) holds
 * downloads back with a reason, and [stopAll] parks running downloads when the system ends the
 * background service.
 */
class DownloadQueue(
    private val downloads: DownloadRepository,
    private val folders: FolderRepository,
    private val archives: ArchiveRepository,
    private val progress: ProgressRepository,
    private val readerSettings: ReaderSettingsRepository,
    private val sources: suspend (String) -> DownloadSource?,
    private val tree: (LibraryFolder) -> DocumentTree?,
    private val stagingRoot: File,
    private val cache: PageCache?,
    private val app: String,
    private val clock: Clock = Clock.SYSTEM,
    /** Why downloads must wait now (e.g. [DownloadReason.WAITING_WIFI]), or null. */
    private val blockedReason: () -> String? = { null },
    private val imageTimeoutMs: Long = 20_000,
    /** Free and total bytes of the volume holding the staging folder. */
    private val freeSpace: (File) -> Pair<Long, Long> = { it.usableSpace to it.totalSpace },
) {
    private class Worker(val id: Long) {
        @Volatile var stopAs: DownloadState? = null
        @Volatile var stopReason: String? = null
        lateinit var job: Job
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val workers = ConcurrentHashMap<String, Worker>()
    private val pumpLock = Mutex()
    private val _changes = MutableSharedFlow<DownloadRow>(extraBufferCapacity = 256)
    @Volatile private var closed = false
    /** Set by [stopAll]: nothing starts again until [start]. */
    @Volatile private var stopped = false

    /** A download changed state or progress (pagesDone = -1: removed). */
    val changes: SharedFlow<DownloadRow> = _changes

    /** True while any download is queued or running (the background service stays while true). */
    suspend fun hasWork(): Boolean = downloads.list().any { it.state == DownloadState.QUEUED || it.state == DownloadState.DOWNLOADING || it.state == DownloadState.ASSEMBLING }

    /** Downloads that were running when the app stopped continue; then the queue starts. */
    suspend fun start() {
        stopped = false
        for (row in downloads.list()) {
            if (row.state == DownloadState.DOWNLOADING || row.state == DownloadState.ASSEMBLING ||
                (row.state == DownloadState.QUEUED && row.errorKind == DownloadReason.STOPPED_BY_SYSTEM)
            ) downloads.setState(row.id, DownloadState.QUEUED, at = clock.now())
        }
        pump()
    }

    /** Queues a gallery (from getGallery) for the target folder; returns the download id. */
    suspend fun enqueue(sourceId: String, gallery: JsonObject, settings: Map<String, String>?): Long {
        val key = GalleryKey(sourceId, gallery["id"]!!.jsonPrimitive.content)
        downloads.activeFor(key)?.let { existing ->
            if (existing.state in setOf(DownloadState.PAUSED, DownloadState.LIMITED, DownloadState.FAILED)) resume(existing.id)
            return existing.id
        }
        val folder = folders.list().firstOrNull { it.isTarget } ?: throw NoLibraryFolderException()
        val id = downloads.add(DownloadRow(
            key = key,
            title = (gallery["title"] as? JsonPrimitive)?.contentOrNull ?: key.galleryId,
            galleryJson = gallery.toString(),
            settingsJson = Json.encodeToString(SETTINGS, settings.orEmpty()),
            folderId = folder.id,
            pageCount = (gallery["pages"] as? JsonArray)?.size ?: 0,
            queuedAt = clock.now(),
        ))
        notify(id)
        pump()
        return id
    }

    suspend fun pause(id: Long) {
        if (stopWorker(id, DownloadState.PAUSED, null)) return
        val row = downloads.get(id) ?: return
        if (row.state == DownloadState.QUEUED) {
            downloads.setState(id, DownloadState.PAUSED, at = clock.now())
            notify(id)
        }
    }

    /** Paused, limited or failed → queued again (a rate limit is cleared: the user chose to retry). */
    suspend fun resume(id: Long) {
        val row = downloads.get(id) ?: return
        if (row.state !in setOf(DownloadState.PAUSED, DownloadState.LIMITED, DownloadState.FAILED)) return
        if (row.state == DownloadState.LIMITED) sources(row.key.sourceId)?.session?.clearLimit()
        downloads.setState(id, DownloadState.QUEUED, at = clock.now())
        notify(id)
        pump()
    }

    /** Stops the download and removes everything it staged; no partial file remains (006 FR-006). */
    suspend fun cancel(id: Long) {
        workers.values.firstOrNull { it.id == id }?.let { w ->
            w.stopAs = null
            w.job.cancelAndJoin()
        }
        val row = downloads.get(id) ?: return
        if (row.state != DownloadState.DONE) withContext(Dispatchers.IO) { staging(id).deleteRecursively() }
        downloads.delete(id)
        _changes.tryEmit(row.copy(state = DownloadState.DONE, pagesDone = -1))
        pump()
    }

    /** The network or a setting changed: held-back downloads may start now. */
    suspend fun conditionsChanged() = pump()

    /**
     * The system ends the background work (service time limit, 007 R12): running downloads stop
     * where they are, marked to continue on the next start.
     */
    suspend fun stopAll(reason: String = DownloadReason.STOPPED_BY_SYSTEM) {
        stopped = true
        for (w in workers.values.toList()) {
            w.stopAs = DownloadState.QUEUED
            w.stopReason = reason
            w.job.cancelAndJoin()
        }
    }

    fun close() {
        closed = true
        for (w in workers.values) {
            w.stopAs = DownloadState.QUEUED
            w.job.cancel()
        }
    }

    /** Starts a worker for every source that has queued downloads and none running. */
    private suspend fun pump() {
        if (closed || stopped) return
        pumpLock.withLock {
            val blocked = blockedReason()
            for (row in downloads.list()) {
                if (row.state != DownloadState.QUEUED || workers.containsKey(row.key.sourceId)) continue
                if (blocked != null) {
                    if (row.errorKind != blocked) {
                        downloads.setState(row.id, DownloadState.QUEUED, blocked, at = clock.now())
                        notify(row.id)
                    }
                    continue
                }
                val source = sources(row.key.sourceId) ?: continue
                if (source.session.state == SessionState.LIMITED) continue
                val worker = Worker(row.id)
                workers[row.key.sourceId] = worker
                worker.job = scope.launch { run(row, source, worker) }
            }
        }
    }

    private fun stopWorker(id: Long, state: DownloadState, reason: String?): Boolean {
        val w = workers.values.firstOrNull { it.id == id } ?: return false
        w.stopAs = state
        w.stopReason = reason
        w.job.cancel()
        return true
    }

    private suspend fun run(row: DownloadRow, source: DownloadSource, worker: Worker) {
        try {
            downloads.setState(row.id, DownloadState.DOWNLOADING, at = clock.now())
            notify(row.id)
            val folder = folders.list().first { it.id == row.folderId }
            val target = tree(folder)?.takeIf { it.isAvailable() } ?: throw IOException("library folder unavailable")
            val dir = staging(row.id).apply { mkdirs() }

            val gallery = Json.parseToJsonElement(row.galleryJson).jsonObject
            val settings = Json.decodeFromString(SETTINGS, row.settingsJson)
            val fingerprint = PageCache.fingerprint(settings)
            val pages = (gallery["pages"] as? JsonArray).orEmpty()
            val done = HashMap(downloads.pages(row.id).associateBy { it.page })
            val missing = done.values.filter { !File(dir, it.file).exists() }.map { it.page }
            if (missing.isNotEmpty()) {
                downloads.removePages(row.id, missing)
                missing.forEach { done.remove(it) }
            }
            // Refused up front when the staged pages would eat into the storage reserve (007 FR-020).
            val (free, total) = freeSpace(stagingRoot)
            if (total > 0 && (pages.size - done.size) * PAGE_ESTIMATE_BYTES > free - PageCache.reserveBytes(total)) {
                downloads.setState(row.id, DownloadState.FAILED, DownloadReason.NO_SPACE, null, at = clock.now())
                return
            }

            // As many pages at once as the source allows requests at once (its politeness applies to each).
            val concurrency = ((source.session.manifest["politeness"] as? JsonObject)?.get("maxConcurrency")?.jsonPrimitive?.int ?: 1).coerceIn(1, 4)
            val slots = Semaphore(concurrency)
            coroutineScope {
                for (index in pages.indices.filter { it !in done }) {
                    launch {
                        slots.withPermit {
                            blockedReason()?.let { reason -> stopWorker(row.id, DownloadState.QUEUED, reason) }
                            val (bytes, located) = fetchPage(source, row.key.sourceId, settings, gallery, index, fingerprint)
                            val extension = ImageFormat.sniff(bytes) ?: "jpg"
                            val size = ImageFormat.size(bytes) ?: located ?: estimated(pages[index])
                            val file = "$index.$extension"
                            val tmp = File(dir, "$file.tmp")
                            tmp.writeBytes(bytes)
                            File(dir, file).delete()
                            check(tmp.renameTo(File(dir, file))) { "cannot stage page ${index + 1}" }
                            val page = DownloadPage(index, file, size.first, size.second)
                            downloads.addPage(row.id, page)
                            synchronized(done) { done[index] = page }
                            notify(row.id)
                        }
                    }
                }
            }

            downloads.setState(row.id, DownloadState.ASSEMBLING, at = clock.now())
            notify(row.id)
            if (done.size < pages.size) throw IOException("pages missing after download")
            assemble(row, source, folder, target, dir, gallery, done.values.sortedBy { it.page })
            dir.deleteRecursively()
            downloads.setState(row.id, DownloadState.DONE, at = clock.now())
        } catch (e: CancellationException) {
            val state = worker.stopAs
            if (state != null) withContext(NonCancellable) { downloads.setState(row.id, state, worker.stopReason, at = clock.now()) }
        } catch (e: PageResolveException) {
            val kind = e.error.kind
            if (kind == SourceErrorKind.RATE_LIMITED || kind == SourceErrorKind.LOGIN_REQUIRED) {
                downloads.setState(row.id, DownloadState.LIMITED, kind, e.error.message,
                    e.error.retryAfterSeconds?.let { clock.now().plusSeconds(it.toLong()) }, clock.now())
            } else {
                downloads.setState(row.id, DownloadState.FAILED, if (e.error.type == "SourceError") kind else "ScriptError", e.error.message, at = clock.now())
            }
        } catch (e: SourceLimitedException) {
            downloads.setState(row.id, DownloadState.LIMITED, SourceErrorKind.RATE_LIMITED, e.message,
                e.retryAfterSeconds?.let { clock.now().plusSeconds(it.toLong()) }, clock.now())
        } catch (e: ImageHttpException) {
            downloads.setState(row.id, DownloadState.FAILED, "Network", "HTTP ${e.status}", at = clock.now())
        } catch (e: PageTimeoutException) {
            downloads.setState(row.id, DownloadState.FAILED, "Network", null, at = clock.now())
        } catch (e: IOException) {
            downloads.setState(row.id, DownloadState.FAILED, "Disk", e.message, at = clock.now())
        } catch (e: IllegalStateException) {
            downloads.setState(row.id, DownloadState.FAILED, "Disk", e.message, at = clock.now())
        } finally {
            withContext(NonCancellable) {
                workers.remove(row.key.sourceId, worker)
                if (downloads.get(row.id) != null) notify(row.id)
                pump()
            }
        }
    }

    private suspend fun fetchPage(
        source: DownloadSource, sourceId: String, settings: Map<String, String>, gallery: JsonObject, index: Int, fingerprint: String,
    ): Pair<ByteArray, Pair<Int, Int>?> {
        val cacheKey = PageCache.key(sourceId, gallery["id"]!!.jsonPrimitive.content, index, fingerprint)
        cache?.get(cacheKey)?.let { return it to null }
        var located: Pair<Int, Int>? = null
        val bytes = PageImageLoader(source.session, settings, timeoutMs = imageTimeoutMs).load(gallery, index, PageAttempts()) { image ->
            val o = image as? JsonObject
            val w = (o?.get("width") as? JsonPrimitive)?.doubleOrNull
            val h = (o?.get("height") as? JsonPrimitive)?.doubleOrNull
            if (w != null && h != null && w > 0 && h > 0) located = w.toInt() to h.toInt()
        }
        cache?.put(cacheKey, bytes)
        return bytes to located
    }

    private suspend fun assemble(
        row: DownloadRow, source: DownloadSource, folder: LibraryFolder, target: DocumentTree, dir: File, g: JsonObject, pages: List<DownloadPage>,
    ) {
        val now = clock.now()
        var metadata = ArchiveMetadata(
            app = app,
            source = ArchiveMetadata.Source(row.key.sourceId, source.name, row.key.galleryId, g.s("webUrl")),
            title = row.title,
            titleOriginal = g.s("titleOriginal"),
            language = g.s("language"),
            type = g.s("type"),
            uploader = g.s("uploader"),
            uploadedAt = g.s("uploadedAt")?.let { runCatching { java.time.OffsetDateTime.parse(it).toInstant() }.getOrNull() },
            tags = (g["tags"] as? JsonArray).orEmpty().mapNotNull { (it as? JsonObject)?.let { t -> t.s("name")?.let { n -> ArchiveMetadata.Tag(t.s("namespace"), n) } } },
            savedAt = now,
            pages = pages.map { ArchiveMetadata.Page(CbzWriter.pageName(it.page, pages.size, it.file.substringAfterLast('.')), it.width, it.height) },
        )
        progress.get(row.key)?.let { metadata = metadata.copy(progress = ArchiveMetadata.ProgressBlock(it.page, it.offset, it.finished, it.updatedAt)) }
        readerSettings.get(row.key)?.let { metadata = metadata.copy(reader = ArchiveMetadata.ReaderBlock(it.zoom, it.fitWidth, it.gaps, it.updatedAt)) }

        val name = CbzWriter.uniqueName(CbzWriter.safeName(metadata.title), target.listFiles().map { it.name }.toSet())
        val partial = target.create(name.removeSuffix(CbzWriter.EXTENSION) + CbzWriter.PART_EXTENSION, "application/octet-stream")
        try {
            target.outputStream(partial).use { out -> CbzWriter.write(out, pages.map { File(dir, it.file) }, metadata) }
        } catch (e: Throwable) {
            target.delete(partial)
            throw e
        }
        val relPath = target.rename(partial, name)
        val entry = target.stat(relPath)
        archives.upsert(ArchiveRow(
            folderId = folder.id, relPath = relPath, size = entry?.size ?: 0, modifiedAt = entry?.modifiedAt ?: now, key = row.key,
            title = metadata.title, titleOriginal = metadata.titleOriginal, language = metadata.language, type = metadata.type,
            tags = metadata.tags.map { ArchiveTag(it.namespace, it.name) }, pages = metadata.pages.map { ArchivePage(it.file, it.width, it.height) },
            savedAt = now, format = "hentai-reader-1",
        ))
    }

    private fun staging(id: Long) = File(stagingRoot, "partial/$id")

    private suspend fun notify(id: Long) {
        downloads.get(id)?.let { _changes.tryEmit(it) }
    }

    private companion object {
        val SETTINGS = MapSerializer(String.serializer(), String.serializer())

        fun estimated(page: JsonElement): Pair<Int, Int> {
            val o = page as? JsonObject
            return maxOf(1, ((o?.get("width") as? JsonPrimitive)?.doubleOrNull ?: 2.0).toInt()) to
                maxOf(1, ((o?.get("height") as? JsonPrimitive)?.doubleOrNull ?: 3.0).toInt())
        }

        fun JsonObject.s(k: String) = (this[k] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
    }
}

/** No library folder is set yet: the first save asks for one (007 FR-028). */
/** Estimated size of a page not yet downloaded, for the free-space check. */
private const val PAGE_ESTIMATE_BYTES = 1024L * 1024

class NoLibraryFolderException : Exception("no library folder")
