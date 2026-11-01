package hentaireader.app.system

import android.util.Log
import hentaireader.app.BuildConfig
import hentaireader.host.http.RawRequest
import hentaireader.host.http.RawResponse
import hentaireader.host.http.Transport
import java.util.concurrent.atomic.AtomicInteger

/**
 * Counts requests sent while the app is not on screen (007 SC-007, T078). Downloads are the only
 * work allowed then; anything else counted here is a bug. Debug builds log the host (never the
 * address or cookies) of each such request.
 */
class BackgroundAudit(private val inner: Transport, private val shown: () -> Boolean) : Transport {
    val backgroundRequests = AtomicInteger()

    override suspend fun send(request: RawRequest): RawResponse {
        if (!shown()) {
            val n = backgroundRequests.incrementAndGet()
            if (BuildConfig.DEBUG) Log.d(TAG, "request #$n while in the background: ${request.url.substringAfter("://").substringBefore('/')}")
        }
        return inner.send(request)
    }

    private companion object {
        const val TAG = "HR.Background"
    }
}
