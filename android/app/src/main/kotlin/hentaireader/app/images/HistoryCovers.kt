package hentaireader.app.images

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import hentaireader.app.AppServices
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveState
import hentaireader.host.data.HistoryEntry
import hentaireader.host.data.HistoryRepository
import hentaireader.host.storage.PageCache
import java.io.File
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * Covers for history entries (port of the Windows HistoryCovers; 006 R11, 007 T070): from the
 * saved copy or the page cache's first page, made once into a small JPEG (≤ 300 px, quality 85) in
 * `covers/`; from the network only with "Load covers from the network" (off by default), one entry
 * at a time, so History makes no requests unless asked to.
 */
class HistoryCovers(private val services: AppServices) {
    private val dir = File(services.dataDir, "covers")
    private val memory = object : LruCache<String, ImageBitmap>(6 * 1024 * 1024) {
        override fun sizeOf(key: String, value: ImageBitmap) = value.width * value.height * 4
    }
    private val network = Mutex()

    suspend fun load(entry: HistoryEntry): ImageBitmap? {
        val key = entry.key.toString()
        memory.get(key)?.let { return it }
        val file = entry.coverFile?.let { File(dir, it) }?.takeIf { it.exists() }
            ?: create(entry)?.let { File(dir, it) }
            ?: return null
        return withContext(Dispatchers.IO) { BitmapFactory.decodeFile(file.path)?.asImageBitmap() }?.also { memory.put(key, it) }
    }

    /** Drops the decoded covers (low memory); they are read from disk again when shown. */
    fun clearMemory() = memory.evictAll()

    fun forget(coverFile: String?) {
        coverFile?.let { File(dir, it).delete() }
    }

    private suspend fun create(entry: HistoryEntry): String? {
        val bytes = fromSavedCopy(entry) ?: fromCache(entry) ?: if (services.prefs.value.networkCovers) fromNetwork(entry) else null
        bytes ?: return null
        val name = sha(entry.key.toString()).take(24) + ".jpg"
        val ok = withContext(Dispatchers.IO) {
            val decoded = decodeSmall(bytes) ?: return@withContext false
            dir.mkdirs()
            File(dir, name).outputStream().use { decoded.compress(Bitmap.CompressFormat.JPEG, 85, it) }
            true
        }
        if (!ok) return null
        HistoryRepository(services.database()).setCover(entry.key, name)
        return name
    }

    private suspend fun fromSavedCopy(entry: HistoryEntry): ByteArray? {
        val item = ArchiveRepository(services.database()).find(entry.key).firstOrNull { it.archive.state == ArchiveState.OK } ?: return null
        return runCatching { services.openArchive(item).use { r -> withContext(Dispatchers.IO) { r.readEntry(r.pages.first()) } } }.getOrNull()
    }

    private suspend fun fromCache(entry: HistoryEntry): ByteArray? = withContext(Dispatchers.IO) {
        val fingerprint = PageCache.fingerprint(services.prefs.value.sourceSettings[entry.key.sourceId])
        services.pageCache().get(PageCache.key(entry.key.sourceId, entry.key.galleryId, 0, fingerprint))
    }

    /** getGallery, then its cover image through the source (one entry at a time). */
    private suspend fun fromNetwork(entry: HistoryEntry): ByteArray? = network.withLock {
        runCatching {
            val session = services.session(entry.key.sourceId)
            val result = services.call(session, entry.key.sourceId, "getGallery", listOf(JsonPrimitive(entry.key.galleryId)))
            val cover = (result.value as? JsonObject)?.get("cover") as? JsonObject ?: return@runCatching null
            session.fetchImage(cover)
        }.getOrNull()
    }

    private fun decodeSmall(bytes: ByteArray): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0) return null
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= MAX_WIDTH) sample *= 2
        val b = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample }) ?: return null
        if (b.width <= MAX_WIDTH) return b
        return Bitmap.createScaledBitmap(b, MAX_WIDTH, b.height * MAX_WIDTH / b.width, true).also { b.recycle() }
    }

    private fun sha(s: String) = MessageDigest.getInstance("SHA-256").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }

    private companion object {
        const val MAX_WIDTH = 300
    }
}
