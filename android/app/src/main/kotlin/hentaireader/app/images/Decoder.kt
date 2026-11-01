package hentaireader.app.images

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.BitmapRegionDecoder
import android.graphics.ImageDecoder
import android.graphics.Rect
import android.os.Build
import java.nio.ByteBuffer
import kotlin.math.ceil
import kotlin.math.roundToInt

/** A decoded page: one bitmap, or a region decoder for tiles decoded near the view only. */
sealed interface DecodedPage {
    val sourceWidth: Int
    val sourceHeight: Int

    class Whole(val bitmap: Bitmap, override val sourceWidth: Int, override val sourceHeight: Int) : DecodedPage {
        val bytes: Long get() = bitmap.width.toLong() * bitmap.height * 4
    }

    class Tiled(val decoder: BitmapRegionDecoder, override val sourceWidth: Int, override val sourceHeight: Int) : DecodedPage
}

/**
 * Page decoding for the reader (007 FR-007 via 005, FR-018, research R7): at the displayed width,
 * never wider than the image itself; a page whose decode would exceed [maxBytes] or a texture-safe
 * height is not decoded whole but kept as a region decoder, and the reader decodes tiles of it
 * near the view. Runs on a background dispatcher (callers).
 */
object PageDecoder {
    /** Taller decodes are tiled (GPU texture limits are 4096–16384 px; stay safely below). */
    private const val MAX_WHOLE_HEIGHT = 4096

    /** Pixel size of an encoded image, or null when it cannot be read. */
    fun bounds(bytes: ByteArray): Pair<Int, Int>? {
        val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, o)
        return if (o.outWidth > 0 && o.outHeight > 0) o.outWidth to o.outHeight else null
    }

    /** Decodes for a display [targetWidth] (px); throws when the bytes are not an image. */
    fun decode(bytes: ByteArray, targetWidth: Int, maxBytes: Long): DecodedPage {
        val (w, h) = bounds(bytes) ?: throw IllegalArgumentException("not an image")
        val width = minOf(targetWidth, w).coerceAtLeast(1)
        val height = (width.toDouble() * h / w).roundToInt().coerceAtLeast(1)
        if (height > MAX_WHOLE_HEIGHT || width.toLong() * height * 4 > maxBytes) {
            return DecodedPage.Tiled(regionDecoder(bytes), w, h)
        }
        val bitmap = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            ImageDecoder.decodeBitmap(ImageDecoder.createSource(ByteBuffer.wrap(bytes))) { decoder, _, _ ->
                decoder.setTargetSize(width, height)
                decoder.allocator = ImageDecoder.ALLOCATOR_HARDWARE
            }
        } else {
            val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample(w, width) })
                ?: throw IllegalArgumentException("cannot decode image")
            if (decoded.width > width * 1.2) Bitmap.createScaledBitmap(decoded, width, height, true).also { decoded.recycle() } else decoded
        }
        return DecodedPage.Whole(bitmap, w, h)
    }

    /**
     * One tile of a tiled page: source rows [top, bottom) decoded for [targetWidth]; the result
     * may be wider (power-of-two sampling) and is scaled when drawn.
     */
    fun tile(page: DecodedPage.Tiled, top: Int, bottom: Int, targetWidth: Int): Bitmap? {
        val rect = Rect(0, top.coerceIn(0, page.sourceHeight), page.sourceWidth, bottom.coerceIn(0, page.sourceHeight))
        if (rect.isEmpty) return null
        val options = BitmapFactory.Options().apply { inSampleSize = sample(page.sourceWidth, targetWidth) }
        return page.decoder.decodeRegion(rect, options)
    }

    /** Number of tiles for a page shown [displayHeight] px tall, each about [tileHeight] px. */
    fun tileCount(displayHeight: Double, tileHeight: Double): Int = maxOf(1, ceil(displayHeight / tileHeight).toInt())

    private fun regionDecoder(bytes: ByteArray): BitmapRegionDecoder =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) BitmapRegionDecoder.newInstance(bytes, 0, bytes.size)
        else @Suppress("DEPRECATION") BitmapRegionDecoder.newInstance(bytes, 0, bytes.size, false)!!

    /** Largest power-of-two reduction that keeps the image at least [target] px wide. */
    private fun sample(width: Int, target: Int): Int {
        var s = 1
        while (target > 0 && width / (s * 2) >= target) s *= 2
        return s
    }
}
