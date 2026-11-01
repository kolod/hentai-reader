package hentaireader.host.http

/** A request exactly as sent: lower-case header names (including cookies). */
data class RawRequest(
    val method: String,
    val url: String,
    val headers: Map<String, String>,
    val body: ByteArray?,
    val timeoutMs: Long,
)

/** Response as received: lower-case header names, every set-cookie kept. */
class RawResponse(val status: Int, val headers: List<Pair<String, String>>, val body: ByteArray)

interface Transport {
    suspend fun send(request: RawRequest): RawResponse
}

/** A transport failure that aborts the whole call, whatever the script does. */
open class FatalTransportException(message: String) : Exception(message)

/** Offline replay received a request with no recorded fixture. */
class UnmatchedRequestException(method: String, url: String, range: String?) :
    FatalTransportException("no recorded fixture for $method $url${if (range == null) "" else " (range: $range)"}")
