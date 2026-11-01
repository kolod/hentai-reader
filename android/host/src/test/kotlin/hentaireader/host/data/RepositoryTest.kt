package hentaireader.host.data

import hentaireader.host.Preferences
import java.io.File
import java.nio.file.Files
import java.time.Instant
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Port of the Windows LibraryDatabaseTests and RepositoryTests (same schema and rules). */
class RepositoryTest {
    private val t0: Instant = Instant.parse("2026-10-25T08:00:00Z")

    private fun <T> withDb(block: suspend (LibraryDatabase, File) -> T): T = runBlocking {
        val dir = Files.createTempDirectory("hr-db").toFile()
        try {
            LibraryDatabase.open(File(dir, "library.db")).use { block(it, dir) }
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun `creates the schema once and reopens`() = runBlocking {
        val dir = Files.createTempDirectory("hr-db").toFile()
        val file = File(dir, "nested/library.db")
        LibraryDatabase.open(file).use { db -> HistoryRepository(db).touch(GalleryKey("hitomi", "1"), "G", 3, t0) }
        LibraryDatabase.open(file).use { db -> assertEquals(1, HistoryRepository(db).count()) }
        dir.deleteRecursively()
        Unit
    }

    @Test
    fun `refuses a newer schema`() = runBlocking {
        val dir = Files.createTempDirectory("hr-db").toFile()
        val file = File(dir, "library.db")
        LibraryDatabase.open(file).use { it.sql { exec("PRAGMA user_version = 99") } }
        assertThrows<IllegalStateException> { runBlocking { LibraryDatabase.open(file) } }
        dir.deleteRecursively()
        Unit
    }

    @Test
    fun `progress keeps finished and clamps`() = withDb { db, _ ->
        val repo = ProgressRepository(db)
        val key = GalleryKey("hitomi", "1")
        repo.set(key, Progress(9, 1.0, 10, true, t0))
        repo.set(key, Progress(50, 2.0, 10, false, t0.plusSeconds(60)))
        val p = repo.get(key)!!
        assertEquals(Triple(9, 1.0, true), Triple(p.page, p.offset, p.finished))
    }

    @Test
    fun `one target folder, the first added, a removed target passes to the oldest`() = withDb { db, _ ->
        val repo = FolderRepository(db)
        val a = repo.add("content://tree/a", "Internal › a", t0)
        val b = repo.add("content://tree/b", "Internal › b", t0)
        assertTrue(a.isTarget)
        assertFalse(b.isTarget)
        assertEquals(b.id, repo.add("content://tree/b", "again", t0).id)

        repo.setTarget(b.id)
        assertEquals(listOf(b.id), repo.list().filter { it.isTarget }.map { it.id })
        repo.remove(b.id)
        assertTrue(repo.list().single().isTarget)
        // Android: the last folder may go; the next save asks for one again (007 FR-028).
        repo.remove(a.id)
        assertTrue(repo.list().isEmpty())
    }

    @Test
    fun `history is newest first with progress and saved mark`() = withDb { db, _ ->
        val history = HistoryRepository(db)
        history.touch(GalleryKey("hitomi", "1"), "Old", 10, t0)
        history.touch(GalleryKey("hitomi", "2"), "New", 20, t0.plusSeconds(3600))
        history.touch(GalleryKey("hitomi", "1"), "Old again", 10, t0.plusSeconds(7200))
        ProgressRepository(db).set(GalleryKey("hitomi", "2"), Progress(3, 0.0, 20, false, t0))
        val folder = FolderRepository(db).add("content://tree/f", "f", t0)
        ArchiveRepository(db).upsert(archive(folder.id, "2.cbz", GalleryKey("hitomi", "2"), "New", t0))

        val page = history.page(0, 10)
        assertEquals(listOf("Old again", "New"), page.map { it.title })
        assertEquals(3, page[1].progress!!.page)
        assertEquals(listOf(false, true), page.map { it.saved })
        assertNull(history.remove(GalleryKey("hitomi", "1")))
        assertEquals(1, history.count())
        history.clear()
        assertEquals(0, history.count())
    }

    @Test
    fun `a history page of 5000 entries is fast`() = withDb { db, _ ->
        db.sql {
            transaction {
                for (i in 0 until 5000) {
                    exec("INSERT INTO history VALUES ('hitomi', \$g, \$t, NULL, 30, \$u)",
                        "\$g" to i.toString(), "\$t" to "Gallery $i", "\$u" to t0.plusSeconds(i.toLong()))
                }
            }
        }
        val started = System.nanoTime()
        val page = HistoryRepository(db).page(0, 100)
        assertEquals("Gallery 4999", page[0].title)
        val ms = (System.nanoTime() - started) / 1_000_000
        assertTrue(ms < 1000, "$ms ms")
    }

    @Test
    fun `library search, filters and sorts combine`() = withDb { db, _ ->
        val folder = FolderRepository(db).add("content://tree/f", "f", t0)
        val archives = ArchiveRepository(db)
        archives.upsert(archive(folder.id, "a.cbz", GalleryKey("hitomi", "1"), "Alpha glasses", t0))
        archives.upsert(archive(folder.id, "b.cbz", GalleryKey("ehentai", "e/2/abcdef0123"), "Beta", t0.plusSeconds(86_400)))
        archives.upsert(archive(folder.id, "c.cbz", GalleryKey("hitomi", "3"), "Gamma 100%_off", t0.plusSeconds(2 * 86_400)))
        val progress = ProgressRepository(db)
        progress.set(GalleryKey("hitomi", "1"), Progress(9, 1.0, 10, true, t0.plusSeconds(5 * 86_400)))
        progress.set(GalleryKey("hitomi", "3"), Progress(2, 0.0, 10, false, t0.plusSeconds(4 * 86_400)))

        suspend fun titles(q: LibraryQuery) = archives.list(q).map { it.archive.title }

        assertEquals(listOf("Gamma 100%_off", "Beta", "Alpha glasses"), titles(LibraryQuery()))
        assertEquals(listOf("Alpha glasses", "Beta", "Gamma 100%_off"), titles(LibraryQuery(sort = LibrarySort.TITLE)))
        assertEquals(listOf("Alpha glasses", "Gamma 100%_off", "Beta"), titles(LibraryQuery(sort = LibrarySort.LAST_READ)))
        assertEquals(listOf("Alpha glasses"), titles(LibraryQuery(text = "GLASS")))
        assertEquals(listOf("Gamma 100%_off"), titles(LibraryQuery(text = "%_")))
        assertEquals(listOf("Beta"), titles(LibraryQuery(read = ReadFilter.UNREAD)))
        assertEquals(listOf("Gamma 100%_off"), titles(LibraryQuery(sourceId = "hitomi", read = ReadFilter.IN_PROGRESS)))
        assertEquals(listOf("Alpha glasses"), titles(LibraryQuery(read = ReadFilter.FINISHED)))

        assertEquals("c.cbz", archives.find(GalleryKey("hitomi", "3")).single().archive.relPath)
        archives.setFolderAvailable(folder.id, available = false)
        assertTrue(archives.list(LibraryQuery()).all { it.archive.state == ArchiveState.UNAVAILABLE })
    }

    @Test
    fun `downloads track pages and states, including Android reasons`() = withDb { db, _ ->
        val folder = FolderRepository(db).add("content://tree/f", "f", t0)
        val repo = DownloadRepository(db)
        val id = repo.add(DownloadRow(key = GalleryKey("hitomi", "1"), title = "G", galleryJson = "{}", folderId = folder.id, pageCount = 3, queuedAt = t0))

        assertEquals(1, repo.addPage(id, DownloadPage(0, "0.webp", 100, 150)))
        assertEquals(2, repo.addPage(id, DownloadPage(2, "2.webp", 100, 150)))
        assertEquals(2, repo.addPage(id, DownloadPage(2, "2.webp", 100, 150)))
        repo.setState(id, DownloadState.LIMITED, "RateLimited", "limit", t0.plusSeconds(300), t0)
        val row = repo.get(id)!!
        assertEquals(Triple(DownloadState.LIMITED, 2, "RateLimited"), Triple(row.state, row.pagesDone, row.errorKind))
        assertEquals(t0.plusSeconds(300), row.retryAfter)
        repo.setState(id, DownloadState.QUEUED, DownloadReason.WAITING_WIFI)
        assertEquals(DownloadReason.WAITING_WIFI, repo.get(id)!!.errorKind)
        assertEquals(id, repo.activeFor(GalleryKey("hitomi", "1"))!!.id)
        repo.removePages(id, listOf(2))
        assertEquals(listOf(0), repo.pages(id).map { it.page })
        repo.delete(id)
        assertTrue(repo.list().isEmpty())
    }

    @Test
    fun `preferences keep Windows names, Android defaults and ranges`() = runBlocking {
        val dir = Files.createTempDirectory("hr-prefs").toFile()
        val file = File(dir, "preferences.json")
        assertEquals(3, Preferences.load(file).preloadPages)
        assertEquals(512, Preferences.load(file).cacheLimitMb)
        // A Windows file: unknown fields (Window) ignored, out-of-range values clamped.
        file.writeText("""{"Language":"de-DE","PreloadPages":9,"CacheLimitMb":1024,"Window":{"X":1},"DefaultQueries":{"hitomi":{"Text":"female:glasses","Filters":{}}}}""")
        val p = Preferences.load(file)
        assertEquals("de-DE", p.language)
        assertEquals(5, p.preloadPages)
        assertEquals(1024, p.cacheLimitMb)
        assertEquals("female:glasses", p.defaultQueries["hitomi"]!!.text)
        p.copy(wifiOnlyDownloads = true).save(file)
        assertTrue(file.readText().contains("\"WifiOnlyDownloads\": true"))
        assertTrue(Preferences.load(file).wifiOnlyDownloads)
        dir.deleteRecursively()
        Unit
    }

    private fun archive(folderId: Long, rel: String, key: GalleryKey, title: String, saved: Instant) = ArchiveRow(
        folderId = folderId, relPath = rel, size = 100, modifiedAt = saved, key = key, title = title,
        pages = listOf(ArchivePage("0001.webp", 100, 150)), savedAt = saved, format = "hentai-reader-1",
    )
}
