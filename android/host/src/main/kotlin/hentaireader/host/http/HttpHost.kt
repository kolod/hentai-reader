package hentaireader.host.http

import hentaireader.host.engine.Envelope
import hentaireader.host.engine.HttpBridgeResult
import hentaireader.host.engine.SourceErrorKind
import hentaireader.host.engine.toJson
import java.nio.charset.Charset
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonObject
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

data class HttpManifest(val domains: List<String>, val politeness: Politeness)

class RequestLogEntry(
    val method: String,
    val url: String,
    val headers: Map<String, String>,
    val startedAt: Long,
    val refused: String? = null,
) {
    @Volatile var endedAt: Long? = null
    @Volatile var status: Int? = null
}

/**
 * http.request for the Android host: validation, domain allow-list, politeness, cookie jar,
 * redirects, response shaping (port of tools/runner/src/host/http.ts and the Windows HttpHost).
 * The same pipeline serves image requests (contract 1.1: images use the source's cookies and
 * limiter).
 */
class HttpHost(
    private val transport: Transport,
    private val enforcePoliteness: Boolean,
    cookies: CookieJar? = null,
) {
    private val scheduler = Scheduler { if (enforcePoliteness) manifest?.politeness else null }
    @Volatile private var manifest: HttpManifest? = null

    val cookies: CookieJar = cookies ?: CookieJar()
    val log: MutableList<RequestLogEntry> = mutableListOf()
    var onFatal: ((Throwable) -> Unit)? = null

    fun setManifest(manifest: HttpManifest) {
        this.manifest = manifest
    }

    fun isAllowed(url: String): Boolean {
        val m = manifest ?: return false
        val host = url.toHttpUrlOrNull()?.host?.lowercase() ?: return false
        return m.domains.any { d ->
            val pattern = d.lowercase()
            if (pattern.startsWith("*.")) CookieJar.domainMatches(host, pattern.substring(2)) && host != pattern.substring(2)
            else host == pattern
        }
    }

    private class HttpFailure(val kind: String, message: String) : Exception(message)

    /** Bridge entry point: JSON request metadata (+ byte body) → envelope (+ bytes). */
    suspend fun request(metaJson: String, bodyBytes: ByteArray?): HttpBridgeResult = try {
        val (raw, responseType) = validate(Json.parseToJsonElement(metaJson).jsonObject, bodyBytes)
        val (url, response) = perform(raw)
        val headers = shapeHeaders(response)
        if (responseType == "bytes") {
            HttpBridgeResult(Envelope.value(toJson(mapOf("status" to response.status, "url" to url, "headers" to headers, "bytes" to true))), response.body)
        } else {
            HttpBridgeResult(Envelope.value(toJson(mapOf(
                "status" to response.status, "url" to url, "headers" to headers, "text" to decode(response.body, headers["content-type"]),
            ))), null)
        }
    } catch (e: HttpFailure) {
        HttpBridgeResult(Envelope.error(e.kind, e.message ?: ""), null)
    } catch (e: FatalTransportException) {
        onFatal?.invoke(e)
        HttpBridgeResult(Envelope.error(SourceErrorKind.NETWORK, e.message ?: ""), null)
    } catch (e: kotlinx.coroutines.CancellationException) {
        throw e
    } catch (e: Exception) {
        HttpBridgeResult(Envelope.error(SourceErrorKind.NETWORK, e.message ?: e.toString()), null)
    }

    /** Fetches an image for the UI through the same domain check, cookies and limiter. */
    suspend fun fetchImage(url: String, headers: Map<String, String>?): ByteArray {
        val raw = RawRequest("GET", url, headers?.mapKeys { it.key.lowercase() } ?: emptyMap(), null, DEFAULT_TIMEOUT_MS)
        val (_, response) = perform(raw)
        // HTTP 429/509 is a limit signal (Constitution VI), for images as for script requests.
        if (response.status == 429 || response.status == 509) throw ImageRateLimitException(response.status, retryAfter(response), url)
        if (response.status !in 200..299) throw ImageHttpException(response.status, url)
        return response.body
    }

    private fun validate(meta: JsonObject, bodyBytes: ByteArray?): Pair<RawRequest, String> {
        val method = meta.string("method") ?: "GET"
        if (method != "GET" && method != "POST") throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: unsupported method $method")
        val url = meta.string("url")
        if (url == null || !(url.startsWith("http://", ignoreCase = true) || url.startsWith("https://", ignoreCase = true))) {
            throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: absolute http(s) URL required, got ${url ?: "undefined"}")
        }
        if (url.toHttpUrlOrNull() == null) throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: invalid URL $url")

        val headers = LinkedHashMap<String, String>()
        (meta["headers"] as? JsonObject)?.forEach { (k, v) ->
            val str = (v as? JsonPrimitive)?.takeIf { it.isString }?.content
                ?: throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: header $k must be a string")
            headers[k.lowercase()] = str
        }
        var body = bodyBytes
        val bodyNode = meta["body"]
        if (bodyNode is JsonPrimitive && bodyNode.isString) body = bodyNode.content.toByteArray(Charsets.UTF_8)
        else if (bodyNode != null && bodyNode != JsonNull) throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: body must be a string or Uint8Array")
        if (body != null && method != "POST") throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: body is only allowed with POST")

        val responseType = meta.string("responseType") ?: "text"
        if (responseType != "text" && responseType != "bytes") throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: unsupported responseType $responseType")
        val timeout = (meta["timeoutMs"] as? JsonPrimitive)?.doubleOrNull?.coerceIn(1.0, 120_000.0)?.toLong() ?: DEFAULT_TIMEOUT_MS
        return RawRequest(method, url, headers, body, timeout) to responseType
    }

    private suspend fun perform(first: RawRequest): Pair<String, RawResponse> {
        var req = first
        var hop = 0
        while (true) {
            if (!isAllowed(req.url)) {
                synchronized(log) { log.add(RequestLogEntry(req.method, req.url, req.headers, now(), refused = "domain")) }
                throw HttpFailure(SourceErrorKind.INVALID_INPUT, "http.request: domain not declared in manifest: ${req.url.toHttpUrlOrNull()?.host}")
            }
            val headers = LinkedHashMap(req.headers)
            val cookie = CookieJar.mergeCookieHeader(cookies.forUrl(req.url), req.headers["cookie"])
            if (cookie != null) headers["cookie"] = cookie else headers.remove("cookie")
            val sent = req.copy(headers = headers)

            scheduler.acquire()
            val entry = RequestLogEntry(sent.method, sent.url, sent.headers, now())
            synchronized(log) { log.add(entry) }
            val response = try {
                transport.send(sent)
            } finally {
                entry.endedAt = now()
                scheduler.release()
            }
            entry.status = response.status
            cookies.store(req.url, response.headers.filter { it.first == "set-cookie" }.map { it.second })

            val location = response.headers.firstOrNull { it.first == "location" }?.second
            if (response.status !in REDIRECTS || location == null) return req.url to response
            if (hop >= MAX_REDIRECTS) throw HttpFailure(SourceErrorKind.NETWORK, "http.request: more than $MAX_REDIRECTS redirects")
            val toGet = response.status == 303 || (response.status in setOf(301, 302) && req.method == "POST")
            val next = req.url.toHttpUrlOrNull()?.resolve(location)?.toString()
                ?: throw HttpFailure(SourceErrorKind.NETWORK, "http.request: invalid redirect location $location")
            req = req.copy(url = next, method = if (toGet) "GET" else req.method, body = if (toGet) null else req.body)
            hop++
        }
    }

    companion object {
        private val REDIRECTS = setOf(301, 302, 303, 307, 308)
        private const val MAX_REDIRECTS = 5
        const val DEFAULT_TIMEOUT_MS = 30_000L

        private fun JsonObject.string(key: String) = (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull

        private fun shapeHeaders(response: RawResponse): Map<String, String> {
            val headers = LinkedHashMap<String, String>()
            for ((name, value) in response.headers) {
                if (name == "set-cookie") continue
                headers[name] = headers[name]?.let { "$it, $value" } ?: value
            }
            return headers
        }

        private val CHARSET = Regex("charset=([^;]+)", RegexOption.IGNORE_CASE)

        private fun decode(body: ByteArray, contentType: String?): String {
            val charset = contentType?.let { CHARSET.find(it)?.groupValues?.get(1)?.trim()?.trim('"') }
            return try {
                String(body, if (charset == null) Charsets.UTF_8 else Charset.forName(charset))
            } catch (_: Exception) {
                String(body, Charsets.UTF_8)
            }
        }

        /** Retry-After in seconds (delta form), or null when absent or a date. */
        private fun retryAfter(response: RawResponse): Double? =
            response.headers.firstOrNull { it.first == "retry-after" }?.second?.toDoubleOrNull()?.takeIf { it >= 0 }

        private fun now() = System.currentTimeMillis()
    }
}

/** An image request answered with HTTP 429 or 509: the site limits this client. */
class ImageRateLimitException(val status: Int, val retryAfterSeconds: Double?, url: String) : Exception("HTTP $status for $url")

/** An image request answered with another non-success status. */
class ImageHttpException(val status: Int, url: String) : Exception("HTTP $status for $url")
