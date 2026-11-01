package hentaireader.host.reading

import hentaireader.host.data.GalleryKey
import hentaireader.host.sources.SessionState
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.PageCache
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * Where the reader's page bytes come from (006 research R8): online through the source, or from a
 * saved archive. The strip, layout and load planning are the same for both.
 */
interface PageProvider : AutoCloseable {
    val key: GalleryKey
    val title: String
    /** Known or estimated page sizes in reading order (0 × 0 = unknown, 2:3 is assumed). */
    val sizes: List<Pair<Double, Double>>
    /** Tags of the gallery (webtoons open without gaps, 005 FR-004b). */
    val tags: List<String>
    /** Pages loaded at the same time. */
    val concurrency: Int
    val isLimited: Boolean
    val retryAfterSeconds: Double?
    fun clearLimit()

    /** Bytes of one page; [onLocated] receives the exact size when it is learned before the download. */
    suspend fun load(index: Int, page: PageAttempts, onLocated: ((Double?, Double?) -> Unit)?): ByteArray
}

/** Online reading (005): page cache first, else getPageUrl + download through the source. */
class OnlinePageProvider(
    private val session: SourceSession,
    sourceId: String,
    private val settings: Map<String, String>?,
    val gallery: JsonObject,
    private val cache: PageCache?,
) : PageProvider {
    private val fingerprint = PageCache.fingerprint(settings)

    override val key = GalleryKey(sourceId, gallery["id"]!!.jsonPrimitive.content)
    override val title = (gallery["title"] as? JsonPrimitive)?.contentOrNull ?: ""
    override val sizes = (gallery["pages"] as? JsonArray).orEmpty().map { p ->
        val o = p.jsonObject
        ((o["width"] as? JsonPrimitive)?.doubleOrNull ?: 0.0) to ((o["height"] as? JsonPrimitive)?.doubleOrNull ?: 0.0)
    }
    override val tags = (gallery["tags"] as? JsonArray).orEmpty().mapNotNull { ((it as? JsonObject)?.get("name") as? JsonPrimitive)?.contentOrNull }

    // As many pages in flight as the source allows requests at once (005 SC-001).
    override val concurrency = ((session.manifest["politeness"] as? JsonObject)?.get("maxConcurrency")?.jsonPrimitive?.int ?: 2).coerceIn(1, 4)
    override val isLimited get() = session.state == SessionState.LIMITED
    override val retryAfterSeconds get() = session.retryAfterSeconds

    override fun clearLimit() = session.clearLimit()

    override suspend fun load(index: Int, page: PageAttempts, onLocated: ((Double?, Double?) -> Unit)?): ByteArray {
        val cacheKey = PageCache.key(key.sourceId, key.galleryId, index, fingerprint)
        cache?.let { c -> withContext(Dispatchers.IO) { c.get(cacheKey) } }?.let { return it }
        val data = PageImageLoader(session, settings).load(gallery, index, page) { image ->
            val o = image as? JsonObject
            onLocated?.invoke((o?.get("width") as? JsonPrimitive)?.doubleOrNull, (o?.get("height") as? JsonPrimitive)?.doubleOrNull)
        }
        cache?.put(cacheKey, data)
        return data
    }

    override fun close() {}
}

/** A saved gallery: entries read straight from the archive, off the main thread, no network (006 FR-012). */
class ArchivePageProvider(
    private val reader: hentaireader.host.archive.CbzReader,
    override val key: GalleryKey,
    override val title: String,
    /** Page sizes from the library (metadata or measured); used when they match the archive. */
    pages: List<hentaireader.host.data.ArchivePage>?,
    override val tags: List<String> = emptyList(),
) : PageProvider {
    private val byName = pages.orEmpty().associateBy { it.file }
    override val sizes = reader.pages.map { e -> byName[e.name]?.let { it.width.toDouble() to it.height.toDouble() } ?: (0.0 to 0.0) }
    override val concurrency = 3
    override val isLimited = false
    override val retryAfterSeconds: Double? = null
    override fun clearLimit() {}

    override suspend fun load(index: Int, page: PageAttempts, onLocated: ((Double?, Double?) -> Unit)?): ByteArray =
        withContext(Dispatchers.IO) { reader.readEntry(reader.pages[index]) }

    override fun close() = reader.close()
}
