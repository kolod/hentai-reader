package hentaireader.host.reading

import hentaireader.host.engine.CallError
import hentaireader.host.http.ImageHttpException
import hentaireader.host.sources.SourceSession
import java.io.IOException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** What a page remembers between attempts; the next RetryHint is built from it. */
class PageAttempts {
    /** Last PageImage from the source (RetryHint.previous). */
    var pageImage: JsonElement? = null
    /** Failed attempts so far (RetryHint.attempt of the next try). */
    var attempts = 0
    /** HTTP status of the last failed image fetch; null for network failures and timeouts. */
    var failedStatus: Int? = null
}

/** getPageUrl failed; [error] is the source's classified error. */
class PageResolveException(val error: CallError) : Exception(error.message)

/** The image did not arrive within the timeout after the automatic retries. */
class PageTimeoutException(message: String) : Exception(message)

/**
 * Resolves a page through the source (getPageUrl, with a RetryHint after failures) and downloads
 * its image (port of the Windows PageImageLoader; 005 FR-020, FR-020a). A download that takes
 * longer than [timeoutMs] is abandoned and the source asked again; the same after an HTTP error of
 * the image server (status in the hint, growing pause). After [autoRetries] such attempts the
 * error leaves the page to the user's Retry. Limits (429/509) are not retried: they stop the source.
 */
class PageImageLoader(
    private val session: SourceSession,
    private val settings: Map<String, String>? = null,
    private val timeoutMs: Long = 20_000,
    private val autoRetries: Int = 2,
    private val retryDelayMs: Long = 1_000,
) {
    /** [onResolved] is called with every PageImage before its download (exact sizes). */
    suspend fun load(gallery: JsonObject, index: Int, page: PageAttempts, onResolved: ((JsonElement) -> Unit)?): ByteArray {
        while (true) {
            val result = session.call("getPageUrl", listOf(gallery, JsonPrimitive(index), retryHint(page) ?: JsonNull), settings)
            if (!result.ok) throw PageResolveException(result.error!!)
            val image = result.value!!
            page.pageImage = image
            onResolved?.invoke(image)
            try {
                return withTimeout(timeoutMs) { session.fetchImage(image) }
            } catch (_: TimeoutCancellationException) {
                page.failedStatus = null
                if (page.attempts >= autoRetries) throw PageTimeoutException("page ${index + 1}: image did not arrive in ${timeoutMs / 1000} s")
                page.attempts++
            } catch (e: ImageHttpException) {
                if (!retryable(e.status) || page.attempts >= autoRetries) throw e
                page.failedStatus = e.status
                page.attempts++
                delay(retryDelayMs * (1L shl (page.attempts - 1)))
            } catch (e: IOException) {
                if (page.attempts >= autoRetries) throw e
                page.failedStatus = null
                page.attempts++
                delay(retryDelayMs * (1L shl (page.attempts - 1)))
            }
        }
    }

    companion object {
        private fun retryable(status: Int) = status >= 500 || status in setOf(403, 404, 408, 410)

        /** RetryHint for the next getPageUrl, or null on the first attempt. */
        fun retryHint(page: PageAttempts): JsonObject? {
            val previous = page.pageImage
            if (page.attempts == 0 || previous == null) return null
            return JsonObject(buildMap {
                put("attempt", JsonPrimitive(page.attempts))
                put("previous", previous)
                page.failedStatus?.let { put("status", JsonPrimitive(it)) }
            })
        }
    }
}
