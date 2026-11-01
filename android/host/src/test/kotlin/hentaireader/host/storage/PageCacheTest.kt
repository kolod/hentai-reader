package hentaireader.host.storage

import java.io.File
import java.io.IOException
import java.nio.file.Files
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Page cache: LRU, index rebuild, the free-space reserve (007 FR-020) and the move (port of the Windows CacheMoveTests). */
class PageCacheTest {
    private val dir: File = Files.createTempDirectory("hr-cache").toFile()
    private val a get() = File(dir, "a")
    private val b get() = File(dir, "b")
    private val big = 100L * 1024 * 1024

    @AfterEach
    fun cleanup() {
        dir.deleteRecursively()
    }

    private fun bytes(i: Int) = ByteArray(1000 + i) { i.toByte() }
    private fun files(f: File) = f.listFiles()?.count { !it.name.endsWith(".json") } ?: 0

    private fun filled(count: Int, folder: File = a) = runBlocking {
        PageCache(folder, big).also { c -> repeat(count) { c.put("k$it", bytes(it)) } }
    }

    @Test
    fun `least recently used pages are evicted beyond the limit`() = runBlocking {
        val cache = PageCache(a, 3500)
        cache.put("k1", bytes(1))
        cache.put("k2", bytes(2))
        cache.put("k3", bytes(3))
        assertNotNull(cache.get("k1"))       // k1 used again; k2 is now the oldest
        cache.put("k4", bytes(4))
        assertNull(cache.get("k2"))
        assertNotNull(cache.get("k1"))
        assertTrue(cache.sizeBytes <= 3500)
    }

    @Test
    fun `the index is rebuilt from the files and clear empties the folder`() = runBlocking {
        val cache = filled(5)
        cache.close()
        File(a, "index.json").delete()
        val again = PageCache(a, big)
        repeat(5) { assertArrayEquals(bytes(it), again.get("k$it")) }
        again.clear()
        assertEquals(0L, again.sizeBytes)
        assertEquals(0, files(a))
    }

    @Test
    fun `below the free-space reserve pages are not cached and the notice comes once`() = runBlocking {
        var free = 5L * 1024 * 1024 * 1024
        val total = 32L * 1024 * 1024 * 1024
        val cache = PageCache(a, big, freeSpace = { free to total })
        var notices = 0
        cache.onReserve = { notices++ }
        assertTrue(cache.put("k1", bytes(1)))
        free = 900L * 1024 * 1024            // < min(1 GiB, 3.2 GiB)
        assertFalse(cache.put("k2", bytes(2)))
        assertFalse(cache.put("k3", bytes(3)))
        assertEquals(1, notices)
        assertNull(cache.get("k2"))
        assertNotNull(cache.get("k1"))
    }

    @Test
    fun `reads work during the move and new files go to the new folder`() = runBlocking {
        val cache = filled(30)
        cache.beginMove(b)
        assertEquals(30 to 30, cache.moveProgress)
        assertArrayEquals(bytes(5), cache.get("k5"))
        cache.put("new", bytes(99))
        assertEquals(31, files(a) + files(b))
        assertTrue(cache.moveStep(10))
        repeat(30) { assertArrayEquals(bytes(it), cache.get("k$it"), "k$it") }
        while (cache.moveStep(10)) Unit
        assertNull(cache.movingFrom)
        assertFalse(a.exists())
        assertNotNull(cache.get("new"))
    }

    @Test
    fun `the move resumes after a restart`() = runBlocking {
        val cache = filled(20)
        cache.beginMove(b)
        cache.moveStep(5)
        cache.close()
        val again = PageCache(b, big, moveFrom = a)
        assertEquals(15, again.moveProgress.first)
        repeat(20) { assertNotNull(again.get("k$it"), "k$it") }
        while (again.moveStep(50)) Unit
        assertEquals(20, files(b))
    }

    @Test
    fun `cancel moves files back`() = runBlocking {
        val cache = filled(10)
        cache.beginMove(b)
        cache.moveStep(4)
        cache.beginMove(a)
        assertEquals(4, cache.moveProgress.first)
        while (cache.moveStep(50)) Unit
        assertEquals(a.absolutePath, cache.currentFolder.absolutePath)
        assertEquals(10, files(a))
        repeat(10) { assertNotNull(cache.get("k$it")) }
    }

    @Test
    fun `a failure loses no file`() = runBlocking {
        val cache = filled(10)
        cache.beginMove(b)
        cache.moveStep(3)
        b.deleteRecursively()
        b.writeText("blocked")
        assertThrows<IOException> { runBlocking { cache.moveStep(10) } }
        assertEquals(7, (0 until 10).count { cache.get("k$it") != null })
        assertEquals(7, files(a))
    }

    @Test
    fun `the fingerprint is order-independent and matches Windows' format`() {
        val f = PageCache.fingerprint(mapOf("site" to "e-hentai", "image_quality" to "resampled"))
        assertEquals(f, PageCache.fingerprint(mapOf("image_quality" to "resampled", "site" to "e-hentai")))
        assertEquals(16, f.length)
        assertTrue(f.all { it in "0123456789abcdef" })
    }
}
