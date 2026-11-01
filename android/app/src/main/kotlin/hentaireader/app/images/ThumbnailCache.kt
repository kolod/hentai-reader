package hentaireader.app.images

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.BitmapRegionDecoder
import android.graphics.Rect
import android.os.Build
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import hentaireader.host.sources.SourceSession
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonPrimitive

/**
 * Covers and page thumbnails (007 T037, resource-budgets.md): fetched through the source session
 * (its cookies, limiter and headers, contract 1.1), decoded at the size they are shown, sprite
 * cells cropped to their declared rectangle. Decoded images share a 16 MiB LRU; responses are kept
 * in a small LRU and shared between requests, so the cells of one sprite sheet cost one request.
 */
class ThumbnailCache {
    private val bitmaps = object : LruCache<String, ImageBitmap>(BITMAP_BYTES) {
        override fun sizeOf(key: String, value: ImageBitmap) = value.width * value.height * 4
    }
    private val responses = object : LruCache<String, ByteArray>(RESPONSE_BYTES) {
        override fun sizeOf(key: String, value: ByteArray) = value.size
    }
    private val inflight = HashMap<String, CompletableDeferred<ByteArray>>()

    fun cached(request: JsonObject, widthPx: Int): ImageBitmap? = bitmaps.get(key(request, widthPx))

    /** The image for [request] decoded for [widthPx]; throws when it cannot be fetched or decoded. */
    suspend fun load(session: SourceSession, request: JsonObject, widthPx: Int): ImageBitmap {
        val key = key(request, widthPx)
        bitmaps.get(key)?.let { return it }
        val bytes = fetch(session, request)
        val bitmap = withContext(Dispatchers.Default) { decode(bytes, crop(request), widthPx) }
            ?: throw IllegalStateException("cannot decode image")
        return bitmap.asImageBitmap().also { bitmaps.put(key, it) }
    }

    /** Drops everything (low memory, 007 FR-017). */
    fun clear() {
        bitmaps.evictAll()
        responses.evictAll()
    }

    private suspend fun fetch(session: SourceSession, request: JsonObject): ByteArray {
        val url = request["url"]!!.jsonPrimitive.content
        responses.get(url)?.let { return it }
        val (deferred, owner) = synchronized(inflight) {
            inflight[url]?.let { it to false } ?: CompletableDeferred<ByteArray>().also { inflight[url] = it }.let { it to true }
        }
        if (!owner) return deferred.await()
        try {
            val bytes = session.fetchImage(request)
            responses.put(url, bytes)
            deferred.complete(bytes)
            return bytes
        } catch (e: Throwable) {
            deferred.completeExceptionally(e)
            throw e
        } finally {
            synchronized(inflight) { inflight.remove(url) }
        }
    }

    private companion object {
        const val BITMAP_BYTES = 16 * 1024 * 1024
        const val RESPONSE_BYTES = 6 * 1024 * 1024

        fun crop(request: JsonObject): Rect? = (request["crop"] as? JsonObject)?.let {
            val x = it["x"]!!.jsonPrimitive.int
            val y = it["y"]!!.jsonPrimitive.int
            Rect(x, y, x + it["width"]!!.jsonPrimitive.int, y + it["height"]!!.jsonPrimitive.int)
        }

        fun key(request: JsonObject, widthPx: Int) =
            "${(request["url"] as? JsonPrimitive)?.contentOrNull}|${request["crop"]}|$widthPx"

        /** Largest power-of-two reduction that keeps the image at least [target] px wide. */
        fun sample(width: Int, target: Int): Int {
            var s = 1
            while (target > 0 && width / (s * 2) >= target) s *= 2
            return s
        }

        fun decode(bytes: ByteArray, crop: Rect?, widthPx: Int): Bitmap? {
            if (crop != null) {
                val decoder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) BitmapRegionDecoder.newInstance(bytes, 0, bytes.size)
                else @Suppress("DEPRECATION") BitmapRegionDecoder.newInstance(bytes, 0, bytes.size, false)
                return decoder.let { d ->
                    try {
                        val area = Rect(crop).apply { intersect(0, 0, d.width, d.height) }
                        d.decodeRegion(area, BitmapFactory.Options().apply { inSampleSize = sample(area.width(), widthPx) })
                    } finally {
                        d.recycle()
                    }
                }
            }
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample(bounds.outWidth, widthPx) })
        }
    }
}

/**
 * An image of the source, decoded for the space it gets; until it arrives (or when it fails) a
 * placeholder fills exactly that space, so nothing moves (003 edge cases).
 */
@Composable
fun SourceImage(
    cache: ThumbnailCache,
    session: SourceSession?,
    request: JsonObject?,
    contentDescription: String?,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Crop,
) {
    BoxWithConstraints(modifier.background(MaterialTheme.colorScheme.surfaceVariant)) {
        val widthPx = with(LocalDensity.current) { maxWidth.roundToPx() }.coerceAtLeast(1)
        val image by produceState(request?.let { cache.cached(it, widthPx) }, session, request, widthPx) {
            if (value == null && session != null && request != null) {
                value = runCatching { cache.load(session, request, widthPx) }.getOrNull()
            }
        }
        image?.let { Image(it, contentDescription, Modifier.fillMaxSize(), contentScale = contentScale) } ?: Box(Modifier.fillMaxSize())
    }
}
