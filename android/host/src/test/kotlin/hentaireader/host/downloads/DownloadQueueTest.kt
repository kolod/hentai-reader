package hentaireader.host.downloads

import hentaireader.host.archive.CbzReader
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.DownloadReason
import hentaireader.host.data.DownloadRepository
import hentaireader.host.data.DownloadRow
import hentaireader.host.data.DownloadState
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.LibraryDatabase
import hentaireader.host.data.LibraryQuery
import hentaireader.host.data.ProgressRepository
import hentaireader.host.data.ReaderSettingsRepository
import hentaireader.host.http.RawRequest
import hentaireader.host.http.RawResponse
import hentaireader.host.http.Transport
import hentaireader.host.parity.Repo
import hentaireader.host.platform.FileSeekableSource
import hentaireader.host.platform.LocalDocumentTree
import hentaireader.host.sources.SessionOptions
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.PageCache
import hentaireader.host.storage.SourceStorage
import java.io.File
import java.nio.file.Files
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * Saving galleries with the real ehentai.js against a fake site (port of the Windows
 * DownloadQueueTests): viewer pages name one image per page; images are tiny PNGs whose header
 * carries the page size.
 */
class DownloadQueueTest {
    private val dir: File = Files.createTempDirectory("hr-dl").toFile()
    private val token = "0123456789"
    private val gid = "e/1234567/$token"

    @AfterEach
    fun cleanup() {
        dir.deleteRecursively()
    }

    private class Site : Transport {
        val requests = mutableListOf<String>()
        @Volatile var viewerStatus = 200
        @Volatile var allowViewers = Int.MAX_VALUE
        @Volatile var imageDelayMs = 0L
        private val viewers = AtomicInteger()

        override suspend fun send(request: RawRequest): RawResponse {
            synchronized(requests) { requests.add(request.url) }
            val url = request.url
            if (url.startsWith("https://e-hentai.org/s/")) {
                if (viewers.incrementAndGet() > allowViewers) return RawResponse(viewerStatus, emptyList(), ByteArray(0))
                val page = url.substringAfterLast('-').substringBefore('?').toInt()
                return RawResponse(200, listOf("content-type" to "text/html"), """
                    <div id="i2"><div>${"%03d".format(page)}.png :: ${100 + page} x 150 :: 1 KiB</div></div>
                    <img id="img" src="https://a.hath.network/h/$page.png" />
                """.trimIndent().toByteArray())
            }
            if (url.startsWith("https://a.hath.network/h/")) {
                if (imageDelayMs > 0) delay(imageDelayMs)
                val page = url.substringAfterLast('/').substringBefore('.').toInt()
                return RawResponse(200, listOf("content-type" to "image/png"), png(100 + page, 150))
            }
            return RawResponse(404, emptyList(), ByteArray(0))
        }

        fun count(part: String) = synchronized(requests) { requests.count { part in it } }
    }

    private class Env(val db: LibraryDatabase, val queue: DownloadQueue, val site: Site, val cache: PageCache, val lib: File, val session: SourceSession) {
        val downloads = DownloadRepository(db)
        val archives = ArchiveRepository(db)
    }

    private fun gallery(pages: Int, id: String = gid, title: String = "Saved gallery"): JsonObject = Json.parseToJsonElement("""
        {"id":"$id","title":"$title","language":"en","type":"manga","webUrl":"https://e-hentai.org/g/1234567/$token/",
         "tags":[{"namespace":"other","name":"full color"}],
         "pages":[${(0 until pages).joinToString(",") { """{"index":$it,"width":2,"height":3}""" }}],
         "sourceData":{"pages":[${(1..pages).joinToString(",") { """["abcdef0123",$it]""" }}]}}
    """.trimIndent()).jsonObject

    private fun env(site: Site = Site(), blocked: () -> String? = { null }): Env = runBlocking {
        val db = LibraryDatabase.open(File(dir, "library.db"))
        val lib = File(dir, "lib").apply { mkdirs() }
        FolderRepository(db).add(lib.path, "lib", Instant.now())
        val session = SourceSession.open(File(Repo.sources, "ehentai/ehentai.js").readText(), "ehentai.js",
            SessionOptions(site, false, SourceStorage(), Repo.scripts))
        val cache = PageCache(File(dir, "cache"), 100L * 1024 * 1024)
        val queue = queue(db, session, cache, blocked)
        Env(db, queue, site, cache, lib, session)
    }

    private fun queue(db: LibraryDatabase, session: SourceSession, cache: PageCache?, blocked: () -> String? = { null },
                      freeSpace: (File) -> Pair<Long, Long> = { 100L shl 30 to (200L shl 30) }) = DownloadQueue(
        DownloadRepository(db), FolderRepository(db), ArchiveRepository(db), ProgressRepository(db), ReaderSettingsRepository(db),
        sources = { id -> if (id == "ehentai") DownloadSource(session, "E-Hentai", null) else null },
        tree = { LocalDocumentTree(File(it.path)) },
        stagingRoot = File(dir, "staging"),
        cache = cache,
        app = "Hentai Reader test (Android)",
        blockedReason = blocked,
        imageTimeoutMs = 5_000,
        freeSpace = freeSpace,
    )

    private suspend fun DownloadQueue.waitFor(id: Long, vararg states: DownloadState): DownloadRow =
        withTimeout(30_000) { changes.first { it.id == id && it.pagesDone >= 0 && it.state in states } }

    @Test
    fun `saves a gallery into one archive with exact sizes`() = runBlocking {
        val e = env()
        val done = async { e.queue.waitFor(1, DownloadState.DONE, DownloadState.FAILED, DownloadState.LIMITED) }
        e.queue.enqueue("ehentai", gallery(5), null)
        val row = done.await()
        assertEquals(DownloadState.DONE to 5, row.state to row.pagesDone)
        val saved = e.archives.find(GalleryKey("ehentai", gid)).single()
        assertEquals("Saved gallery.cbz", saved.archive.relPath)
        assertEquals(listOf(101, 102, 103, 104, 105), saved.archive.pages.map { it.width })
        CbzReader(FileSeekableSource(File(e.lib, saved.archive.relPath), false)).use { r ->
            assertEquals(listOf("0001.png", "0002.png", "0003.png", "0004.png", "0005.png"), r.pages.map { it.name })
            assertArrayEquals(png(104, 150), r.readEntry(r.pages[3]))
            assertEquals("full color", r.readMetadata()!!.tags.single().name)
            assertEquals("Hentai Reader test (Android)", r.readMetadata()!!.app)
        }
        assertFalse(File(dir, "staging/partial/1").exists())
        assertTrue(e.lib.listFiles()!!.none { it.name.endsWith(".partial") })
        assertEquals(5, e.site.count("hath.network"))
        e.queue.close()
    }

    @Test
    fun `pages in the page cache are not requested`() = runBlocking {
        val e = env()
        for (i in 0 until 3) e.cache.put(PageCache.key("ehentai", gid, i, PageCache.fingerprint(null)), png(500 + i, 700))
        val done = async { e.queue.waitFor(1, DownloadState.DONE, DownloadState.FAILED) }
        e.queue.enqueue("ehentai", gallery(3), null)
        assertEquals(DownloadState.DONE, done.await().state)
        assertTrue(e.site.requests.isEmpty())
        e.queue.close()
    }

    @Test
    fun `a rate limit stops and keeps pages, then resume continues`() = runBlocking {
        val site = Site().apply { allowViewers = 2; viewerStatus = 429 }
        val e = env(site)
        val limited = async { e.queue.waitFor(1, DownloadState.LIMITED, DownloadState.FAILED, DownloadState.DONE) }
        e.queue.enqueue("ehentai", gallery(5), null)
        val row = limited.await()
        assertEquals(Triple(DownloadState.LIMITED, 2, "RateLimited"), Triple(row.state, row.pagesDone, row.errorKind))
        val viewersAtLimit = site.count("/s/")
        delay(300)
        assertEquals(viewersAtLimit, site.count("/s/"))
        assertTrue(e.archives.list(LibraryQuery()).isEmpty())

        site.allowViewers = Int.MAX_VALUE
        val done = async { e.queue.waitFor(1, DownloadState.DONE, DownloadState.FAILED, DownloadState.LIMITED) }
        e.queue.resume(1)
        assertEquals(DownloadState.DONE, done.await().state)
        assertEquals(5, site.count("hath.network"))
        e.queue.close()
    }

    @Test
    fun `sign-in required stops the download`() = runBlocking {
        val e = env()
        val stopped = async { e.queue.waitFor(1, DownloadState.LIMITED, DownloadState.FAILED, DownloadState.DONE) }
        e.queue.enqueue("ehentai", gallery(2), mapOf("image_quality" to "original"))
        val row = stopped.await()
        assertEquals(DownloadState.LIMITED to "LoginRequired", row.state to row.errorKind)
        assertTrue(e.archives.list(LibraryQuery()).isEmpty())
        e.queue.close()
    }

    @Test
    fun `a restart continues without fetching pages twice, and cancel leaves nothing`() = runBlocking {
        val site = Site().apply { allowViewers = 3; viewerStatus = 503 }
        val e = env(site)
        val failed = async { e.queue.waitFor(1, DownloadState.FAILED, DownloadState.LIMITED, DownloadState.DONE) }
        e.queue.enqueue("ehentai", gallery(6), null)
        assertEquals(DownloadState.FAILED, failed.await().state)
        e.queue.close()
        e.downloads.setState(1, DownloadState.DOWNLOADING)   // the app stopped during the download

        site.allowViewers = Int.MAX_VALUE
        val again = queue(e.db, e.session, e.cache)   // the app has one page cache
        val done = async { again.waitFor(1, DownloadState.DONE, DownloadState.FAILED) }
        again.start()
        assertEquals(DownloadState.DONE, done.await().state)
        val images = synchronized(site.requests) { site.requests.filter { "hath.network" in it } }
        assertEquals(images.distinct().size, images.size)

        site.allowViewers = site.count("/s/") + 2
        val stopped = async { again.waitFor(2, DownloadState.FAILED, DownloadState.DONE) }
        val id2 = again.enqueue("ehentai", gallery(4, "e/7654321/9876543210", "Cancelled"), null)
        stopped.await()
        again.cancel(id2)
        assertNull(e.downloads.get(id2))
        assertFalse(File(dir, "staging/partial/$id2").exists())
        assertTrue(e.lib.listFiles()!!.none { it.name.startsWith("Cancelled") })
        again.close()
    }

    @Test
    fun `a download that would eat into the storage reserve is refused before any request`() = runBlocking {
        val e = env()
        // 32 GiB volume: reserve 1 GiB; 1.002 GiB free leaves room for 2 of the 5 pages.
        val small = queue(e.db, e.session, e.cache, freeSpace = { (1L shl 30) + (2L shl 20) to (32L shl 30) })
        val failed = async { small.waitFor(1, DownloadState.FAILED, DownloadState.DONE) }
        small.enqueue("ehentai", gallery(5), null)
        val row = failed.await()
        assertEquals(DownloadState.FAILED to DownloadReason.NO_SPACE, row.state to row.errorKind)
        assertEquals(0, e.site.count("hath.network"))
        small.close()
        e.queue.close()
    }

    @Test
    fun `wifi only holds downloads back with a reason and lets them run when allowed`() = runBlocking {
        var blocked: String? = DownloadReason.WAITING_WIFI
        val e = env(blocked = { blocked })
        val id = e.queue.enqueue("ehentai", gallery(2), null)
        val row = e.downloads.get(id)!!
        assertEquals(DownloadState.QUEUED to DownloadReason.WAITING_WIFI, row.state to row.errorKind)
        assertTrue(e.site.requests.isEmpty())
        blocked = null
        val done = async { e.queue.waitFor(id, DownloadState.DONE, DownloadState.FAILED) }
        e.queue.conditionsChanged()
        assertEquals(DownloadState.DONE, done.await().state)
        e.queue.close()
    }

    @Test
    fun `the system stopping the service parks the download and the next start continues it`() = runBlocking {
        val site = Site().apply { imageDelayMs = 150 }
        val e = env(site)
        val first = async { withTimeout(30_000) { e.queue.changes.first { it.pagesDone >= 1 } } }
        e.queue.enqueue("ehentai", gallery(20), null)
        first.await()
        e.queue.stopAll()
        val row = e.downloads.get(1)!!
        assertEquals(DownloadState.QUEUED to DownloadReason.STOPPED_BY_SYSTEM, row.state to row.errorKind)
        val staged = e.downloads.pages(1).map { it.page + 1 }   // images in flight at the stop were never received
        val again = queue(e.db, e.session, e.cache)   // the app has one page cache
        site.imageDelayMs = 0
        val done = async { again.waitFor(1, DownloadState.DONE, DownloadState.FAILED) }
        again.start()
        assertEquals(DownloadState.DONE, done.await().state)
        val images = synchronized(site.requests) { site.requests.filter { "hath.network" in it } }
        for (page in staged) assertEquals(1, images.count { it.endsWith("/$page.png") }, "page $page fetched once")
        assertEquals(20, images.distinct().size)
        again.close()
        e.queue.close()
    }

    companion object {
        fun png(width: Int, height: Int): ByteArray {
            val b = ByteArray(64)
            byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A).copyInto(b)
            "IHDR".toByteArray().copyInto(b, 12)
            for (k in 0 until 4) {
                b[16 + k] = (width shr (24 - 8 * k)).toByte()
                b[20 + k] = (height shr (24 - 8 * k)).toByte()
            }
            return b
        }
    }
}
