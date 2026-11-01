package hentaireader.host.library

import hentaireader.host.archive.ArchiveMetadata
import hentaireader.host.archive.CbzWriter
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveState
import hentaireader.host.data.DownloadRepository
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.LibraryDatabase
import hentaireader.host.data.LibraryQuery
import hentaireader.host.downloads.DownloadQueueTest
import hentaireader.host.platform.LocalDocumentTree
import java.io.File
import java.nio.file.Files
import java.time.Instant
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/** Library scan and folders (port of the Windows LibraryScannerTests and LibraryFoldersTests). */
class LibraryTest {
    private val dir: File = Files.createTempDirectory("hr-lib").toFile()
    private val a = File(dir, "a").apply { mkdirs() }
    private val b = File(dir, "b").apply { mkdirs() }

    @AfterEach
    fun cleanup() {
        dir.deleteRecursively()
    }

    private fun saved(folder: File, name: String, galleryId: String, pages: Int = 2, seed: Int = 0) {
        val staged = List(pages) { File(dir, "p$it-$seed.png").apply { writeBytes(DownloadQueueTest.png(100 + it + seed, 150)) } }
        val m = ArchiveMetadata(
            source = ArchiveMetadata.Source("hitomi", "Hitomi.la", galleryId), title = name, savedAt = Instant.parse("2026-11-01T00:00:00Z"),
            pages = List(pages) { ArchiveMetadata.Page(CbzWriter.pageName(it, pages, "png"), 100 + it + seed, 150) },
        )
        File(folder, "$name.cbz").outputStream().use { CbzWriter.write(it, staged, m) }
    }

    private fun foreign(folder: File, name: String) = ZipOutputStream(File(folder, name).outputStream()).use { zip ->
        for (i in 1..3) { zip.putNextEntry(ZipEntry("$i.png")); zip.write(DownloadQueueTest.png(640, 900 + i)); zip.closeEntry() }
    }

    private class Env(val db: LibraryDatabase) {
        val folders = FolderRepository(db)
        val archives = ArchiveRepository(db)
        val scanner = LibraryScanner(folders, archives, { LocalDocumentTree(File(it.path)) })
        val libraryFolders = LibraryFolders(folders, archives, DownloadRepository(db), { LocalDocumentTree(File(it.path)) })
    }

    private fun env() = runBlocking { Env(LibraryDatabase.open(File(dir, "library.db"))) }

    @Test
    fun `a scan lists app archives with metadata, foreign archives by name and unreadable files`() = runBlocking {
        val e = env()
        val folder = e.folders.add(a.path, "a", Instant.now())
        saved(a, "Saved", "1")
        foreign(a, "Foreign comic.zip")
        File(a, "broken.cbz").writeText("not a zip")
        File(a, "half.cbz.partial").writeText("x")
        val r = e.scanner.scanFolder(folder)
        assertEquals(3, r.added)
        val rows = e.archives.list(LibraryQuery(sort = hentaireader.host.data.LibrarySort.TITLE)).map { it.archive }
        assertEquals(listOf("broken", "Foreign comic", "Saved"), rows.map { it.title })
        assertEquals(GalleryKey("hitomi", "1"), rows[2].key)
        assertEquals("foreign", rows[1].format)
        assertEquals(listOf(901, 902, 903), rows[1].pages.map { it.height })
        assertEquals(ArchiveState.UNREADABLE, rows[0].state)

        assertEquals(3, e.scanner.scanFolder(folder).unchanged)
        File(a, "broken.cbz").delete()
        assertEquals(1, e.scanner.scanFolder(folder).removed)
    }

    @Test
    fun `a foreign archive keeps its identity when renamed, and an unavailable folder marks its galleries`() = runBlocking {
        val e = env()
        val folder = e.folders.add(a.path, "a", Instant.now())
        foreign(a, "x.zip")
        e.scanner.scanFolder(folder)
        val id = e.archives.list(LibraryQuery()).single().archive.key
        File(a, "x.zip").renameTo(File(a, "renamed.cbz"))
        e.scanner.scanFolder(folder)
        assertEquals(id, e.archives.list(LibraryQuery()).single().archive.key)

        a.deleteRecursively()
        assertFalse(e.scanner.scanFolder(folder).available)
        assertTrue(e.archives.list(LibraryQuery()).all { it.archive.state == ArchiveState.UNAVAILABLE })
    }

    @Test
    fun `move and remove copies galleries into the other folder and drops identical duplicates`() = runBlocking {
        val e = env()
        val fa = e.folders.add(a.path, "a", Instant.now())
        val fb = e.folders.add(b.path, "b", Instant.now())
        saved(a, "One", "1")
        saved(a, "Two", "2")
        saved(b, "Two", "2")            // the same gallery, identical pages
        e.scanner.scanAll()
        var last = 0
        e.libraryFolders.moveAndRemove(fa.id, fb.id) { done, _ -> last = done }
        assertEquals(2, last)
        assertEquals(listOf(fb.id), e.folders.list().map { it.id })
        assertTrue(a.listFiles()!!.none { it.name.endsWith(".cbz") })
        assertEquals(setOf("One.cbz", "Two.cbz"), b.listFiles()!!.map { it.name }.filter { it.endsWith(".cbz") }.toSet())
        val rows = e.archives.list(LibraryQuery())
        assertEquals(2, rows.size)
        assertTrue(rows.all { it.archive.folderId == fb.id })
        e.scanner.scanAll()
        assertEquals(2, e.archives.list(LibraryQuery()).size)   // a rescan finds exactly what the move recorded
    }

    @Test
    fun `keep files only removes the folder`() = runBlocking {
        val e = env()
        val fa = e.folders.add(a.path, "a", Instant.now())
        saved(a, "One", "1")
        e.scanner.scanAll()
        e.libraryFolders.removeKeepFiles(fa.id)
        assertTrue(e.folders.list().isEmpty())
        assertTrue(e.archives.list(LibraryQuery()).isEmpty())
        assertTrue(File(a, "One.cbz").exists())
    }
}
