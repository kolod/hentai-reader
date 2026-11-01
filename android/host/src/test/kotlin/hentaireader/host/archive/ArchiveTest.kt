package hentaireader.host.archive

import hentaireader.host.platform.FileSeekableSource
import hentaireader.host.platform.LocalArchiveFile
import java.io.File
import java.nio.file.Files
import java.time.Instant
import java.util.zip.CRC32
import java.util.zip.ZipEntry as JavaZipEntry
import java.util.zip.ZipFile
import java.util.zip.ZipOutputStream
import kotlinx.serialization.json.JsonPrimitive
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Archive format 1 on Android (port of the Windows ArchiveTests, plus the cross-platform layout). */
class ArchiveTest {
    private val dir: File = Files.createTempDirectory("hr-archive").toFile()

    @AfterEach
    fun cleanup() {
        dir.deleteRecursively()
    }

    /** A tiny valid PNG of the given size (header only matters for sniffing and measuring). */
    private fun png(w: Int, h: Int, seed: Int): ByteArray {
        val b = ByteArray(64) { (it * seed).toByte() }
        byteArrayOf(0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A).copyInto(b)
        b[16] = (w shr 24).toByte(); b[17] = (w shr 16).toByte(); b[18] = (w shr 8).toByte(); b[19] = w.toByte()
        b[20] = (h shr 24).toByte(); b[21] = (h shr 16).toByte(); b[22] = (h shr 8).toByte(); b[23] = h.toByte()
        return b
    }

    private fun metadata(pages: Int) = ArchiveMetadata(
        app = "Hentai Reader 0.1.0 (Android)",
        source = ArchiveMetadata.Source("hitomi", "Hitomi.la", "1234567", "https://hitomi.la/g/1234567"),
        title = "Gallery: \"test\"",
        language = "ja",
        type = "manga",
        tags = listOf(ArchiveMetadata.Tag("artist", "someone"), ArchiveMetadata.Tag("female", "glasses")),
        savedAt = Instant.parse("2026-11-01T10:00:00Z"),
        pages = List(pages) { ArchiveMetadata.Page(CbzWriter.pageName(it, pages, "png"), 800, 1200 + it) },
    )

    private fun saved(pages: Int = 3): File {
        val staged = List(pages) { File(dir, "$it.png").apply { writeBytes(png(800, 1200 + it, it + 1)) } }
        val out = File(dir, "g.cbz")
        out.outputStream().use { CbzWriter.write(it, staged, metadata(pages)) }
        return out
    }

    private fun reader(f: File) = CbzReader(FileSeekableSource(f, writable = false))

    @Test
    fun `every entry is stored, pages in reading order, then ComicInfo and metadata`() {
        val file = saved()
        ZipFile(file).use { zip ->
            val names = zip.entries().toList().map { it.name }
            assertEquals(listOf("0001.png", "0002.png", "0003.png", "ComicInfo.xml", "hentai-reader.json"), names)
            assertTrue(zip.entries().toList().all { it.method == JavaZipEntry.STORED })
        }
        reader(file).use { r ->
            assertEquals(listOf("0001.png", "0002.png", "0003.png"), r.pages.map { it.name })
            val m = r.readMetadata()!!
            assertEquals("Gallery: \"test\"", m.title)
            assertEquals(1201, m.pages[1].height)
            assertEquals("png", ImageFormat.sniff(r.readEntry(r.pages[0])))
            assertEquals(800 to 1202, ImageFormat.size(r.readEntry(r.pages[2])))
            val xml = String(r.readEntry(r.find("ComicInfo.xml")!!))
            assertTrue(xml.contains("<Writer>someone</Writer>") && xml.contains("&quot;test&quot;") && xml.contains("Type=\"FrontCover\""))
        }
    }

    @Test
    fun `a metadata update is appended, keeps the images byte-identical and keeps unknown fields`() {
        val file = saved()
        val before = reader(file).use { r -> r.pages.map { r.readEntry(it) } }
        val withExtra = metadata(3).copy(extra = mapOf("futureField" to JsonPrimitive("kept")))
        LocalArchiveFile(file).let { MetadataUpdater.update(it, withExtra.toUtf8()) }
        val updated = withExtra.copy(progress = ArchiveMetadata.ProgressBlock(2, 0.5, false, Instant.parse("2026-11-01T11:00:00Z")))
        assertEquals(MetadataUpdater.Result.APPENDED, MetadataUpdater.update(LocalArchiveFile(file), updated.toUtf8()))
        reader(file).use { r ->
            assertEquals(before.map { it.toList() }, r.pages.map { r.readEntry(it).toList() })
            val m = r.readMetadata()!!
            assertEquals(2, m.progress!!.page)
            assertEquals(JsonPrimitive("kept"), m.extra["futureField"])
        }
        ZipFile(file).use { assertNotNull(it.getEntry("hentai-reader.json")) }   // still a valid ZIP for others
    }

    @Test
    fun `an interrupted update leaves the previous archive readable`() {
        val file = saved()
        val length = file.length()
        val next = metadata(3).copy(progress = ArchiveMetadata.ProgressBlock(1, 0.0, false, Instant.now())).toUtf8()
        MetadataUpdater.update(LocalArchiveFile(file), next)
        val full = file.readBytes()
        for (cut in listOf(length + 10, length + 100, full.size.toLong() - 5)) {
            val copy = File(dir, "cut$cut.cbz").apply { writeBytes(full.copyOf(cut.toInt())) }
            reader(copy).use { r ->
                assertEquals(3, r.pages.size)
                assertNull(r.readMetadata()!!.progress, "cut at $cut falls back to the previous metadata")
            }
            // An update after the crash works on top of the leftover bytes.
            MetadataUpdater.update(LocalArchiveFile(copy), next)
            reader(copy).use { assertEquals(1, it.readMetadata()!!.progress!!.page) }
        }
    }

    @Test
    fun `dead bytes beyond the threshold compact the archive`() {
        val file = saved()
        val big = metadata(3).copy(extra = mapOf("padding" to JsonPrimitive("x".repeat(300_000)))).toUtf8()
        val results = (1..5).map { MetadataUpdater.update(LocalArchiveFile(file), big) }
        assertTrue(MetadataUpdater.Result.REWRITTEN in results)
        assertTrue(file.length() < 3 * big.size, "compacted: ${file.length()}")
        reader(file).use { assertEquals(3, it.readMetadata()!!.pages.size) }
    }

    @Test
    fun `a newer metadata format is not overwritten`() {
        val file = saved()
        MetadataUpdater.update(LocalArchiveFile(file), """{"format":2,"pages":[]}""".toByteArray())
        assertThrows<IllegalStateException> { MetadataUpdater.update(LocalArchiveFile(file), metadata(3).toUtf8()) }
        reader(file).use { assertNull(it.readMetadata()) }
    }

    @Test
    fun `a foreign archive lists pages by natural name order with a stable content id`() {
        fun foreign(f: File) = ZipOutputStream(f.outputStream()).use { zip ->
            for ((name, data) in listOf("10.jpg" to "a", "2.jpg" to "b", "1.jpg" to "c", "notes.txt" to "x", "__MACOSX/1.jpg" to "z")) {
                zip.putNextEntry(JavaZipEntry(name)); zip.write(data.toByteArray()); zip.closeEntry()
            }
        }
        val a = File(dir, "a.zip").also(::foreign)
        val b = File(dir, "renamed.cbz").also(::foreign)
        reader(a).use { r ->
            assertEquals(listOf("1.jpg", "2.jpg", "10.jpg"), r.pages.map { it.name })
            assertNull(r.readMetadata())
            assertArrayEquals("b".toByteArray(), r.readEntry(r.pages[1]))   // deflated entries are inflated
            reader(b).use { assertEquals(r.contentId(), it.contentId()) }
        }
    }

    @Test
    fun `an archive in the Windows layout opens with identity, pages, progress and reader settings`() {
        // Written the way the Windows app writes it (006 contracts/archive-format.md), with
        // generated images: entry names, ComicInfo.xml and metadata from "(Windows)".
        val json = """
            {
              "format": 1,
              "app": "Hentai Reader 0.4.0 (Windows)",
              "source": { "id": "ehentai", "name": "E-Hentai", "galleryId": "e/1/abc", "webUrl": null },
              "title": "Windows gallery", "titleOriginal": null, "language": "en", "type": "doujinshi",
              "uploader": null, "uploadedAt": null, "tags": [],
              "savedAt": "2026-10-25T09:00:00.0000000+00:00",
              "pages": [{ "file": "0001.png", "width": 640, "height": 900 }, { "file": "0002.png", "width": 640, "height": 960 }],
              "progress": { "page": 1, "offset": 0.4, "finished": false, "updatedAt": "2026-10-25T09:30:00+00:00" },
              "reader": { "zoom": 1.3, "fitWidth": false, "gaps": false, "updatedAt": "2026-10-25T09:30:00+00:00" }
            }
        """.trimIndent().toByteArray()
        val file = File(dir, "win.cbz")
        ZipOutputStream(file.outputStream()).use { zip ->
            zip.setMethod(ZipOutputStream.STORED)
            for ((name, data) in listOf("0001.png" to png(640, 900, 3), "0002.png" to png(640, 960, 5), "ComicInfo.xml" to "<ComicInfo/>".toByteArray(), "hentai-reader.json" to json)) {
                zip.putNextEntry(JavaZipEntry(name).apply { method = JavaZipEntry.STORED; size = data.size.toLong(); crc = CRC32().apply { update(data) }.value })
                zip.write(data); zip.closeEntry()
            }
        }
        reader(file).use { r ->
            val m = r.readMetadata()!!
            assertEquals("ehentai" to "e/1/abc", m.source.id to m.source.galleryId)
            assertEquals(listOf(900, 960), m.pages.map { it.height })
            assertEquals(1 to 0.4, m.progress!!.page to m.progress!!.offset)
            assertEquals(1.3, m.reader!!.zoom)
            assertEquals(false, m.reader!!.fitWidth)
        }
    }

    @Test
    fun `file names are safe on Windows and on FAT cards`() {
        assertEquals("a_b_c_d", CbzWriter.safeName("a:b?c|d"))
        assertEquals("_CON", CbzWriter.safeName("CON"))
        assertEquals("gallery", CbzWriter.safeName(" ... "))
        assertTrue(CbzWriter.safeName("漫".repeat(100)).toByteArray().size <= 120)
        assertEquals("x (2).cbz", CbzWriter.uniqueName("x", setOf("x.cbz")))
        assertEquals("x (2).cbz", CbzWriter.uniqueName("x", setOf("x.cbz.partial")))
        assertEquals("0001.webp", CbzWriter.pageName(0, 40, "webp"))
        assertEquals("00001.webp", CbzWriter.pageName(0, 12000, "webp"))
    }
}
