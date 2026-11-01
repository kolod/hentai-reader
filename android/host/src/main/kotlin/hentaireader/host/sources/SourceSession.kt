package hentaireader.host.sources

import hentaireader.host.HostInfo
import hentaireader.host.engine.CallError
import hentaireader.host.engine.CallResult
import hentaireader.host.engine.ContractScripts
import hentaireader.host.engine.EngineLimits
import hentaireader.host.engine.Envelope
import hentaireader.host.engine.HostBridge
import hentaireader.host.engine.HttpBridgeResult
import hentaireader.host.engine.LoadException
import hentaireader.host.engine.SourceEngine
import hentaireader.host.engine.SourceErrorKind
import hentaireader.host.html.HtmlHost
import hentaireader.host.http.CookieJar
import hentaireader.host.http.HttpHost
import hentaireader.host.http.HttpManifest
import hentaireader.host.http.ImageRateLimitException
import hentaireader.host.http.Politeness
import hentaireader.host.http.Transport
import hentaireader.host.storage.SourceStorage
import kotlinx.coroutines.delay
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

enum class SessionState { IDLE, BUSY, LIMITED, BROKEN }

class SessionOptions(
    val transport: Transport,
    val enforcePoliteness: Boolean,
    val storage: SourceStorage,
    val scripts: ContractScripts,
    val cookies: CookieJar? = null,
    val frozenNow: Long? = null,
    val limits: EngineLimits = EngineLimits.DEFAULT,
    val onLog: ((String, String) -> Unit)? = null,
)

/**
 * One running source: engine, HTTP, HTML and storage, implementing the bridge (port of the Windows
 * SourceSession). Limited after RateLimited (no requests until the user retries), Broken after a
 * limit hit (fresh engine on the next call).
 */
class SourceSession private constructor(
    private val script: String,
    private val name: String,
    private val options: SessionOptions,
) : HostBridge, AutoCloseable {
    private val html = HtmlHost()
    private var engine: SourceEngine? = null
    @Volatile private var fatalError: Throwable? = null

    val http = HttpHost(options.transport, options.enforcePoliteness, options.cookies).also { h -> h.onFatal = { if (fatalError == null) fatalError = it } }
    var manifest: JsonObject = JsonObject(emptyMap())
        private set
    var functions: List<String> = emptyList()
        private set
    @Volatile var state: SessionState = SessionState.IDLE
        private set
    @Volatile var retryAfterSeconds: Double? = null
        private set

    private suspend fun startEngine() {
        engine?.close()
        val e = SourceEngine.create(script, name, this, options.scripts, options.frozenNow, options.limits)
        engine = e
        val description = e.describe()
        val m = description.manifest
        val contract = (m["contract"] as? JsonPrimitive)?.contentOrNull
        if (contract == null || !CONTRACT.matches(contract)) throw LoadException("manifest.contract must be \"MAJOR.MINOR\", got $contract")
        if (!isCompatible(HostInfo.CONTRACT_VERSION, contract)) {
            throw LoadException("source requires contract $contract, host supports ${HostInfo.CONTRACT_VERSION.substringBeforeLast('.')}")
        }
        for (field in listOf("id", "name", "version", "domains", "politeness", "login", "filters", "capabilities")) {
            if (m[field] == null) throw LoadException("invalid manifest: missing $field")
        }
        val loginPage = ManifestRules.loginPageViolations(m)
        if (loginPage.isNotEmpty()) throw LoadException("invalid manifest:\n  " + loginPage.joinToString("\n  "))
        manifest = m
        functions = description.functions
        val politeness = m["politeness"]!!.jsonObject
        http.setManifest(HttpManifest(
            (m["domains"] as JsonArray).map { it.jsonPrimitive.content },
            Politeness(politeness["minIntervalMs"]!!.jsonPrimitive.int, politeness["maxConcurrency"]!!.jsonPrimitive.int),
        ))
    }

    /** Calls a source function following the calling convention. */
    suspend fun call(fn: String, args: List<JsonElement?>, settings: Map<String, String>? = null): CallResult {
        if (state == SessionState.LIMITED) {
            return CallResult.failure(CallError("SourceError", SourceErrorKind.RATE_LIMITED, null,
                "the source is rate-limited; retry explicitly", retryAfterSeconds))
        }
        if (state == SessionState.BROKEN || engine?.broken != null) startEngine()
        val callArgs = CallArgs.build(manifest, fn, args, CallArgs.resolveSettings(manifest, settings))
        state = SessionState.BUSY
        fatalError = null
        val result = engine!!.call(fn, callArgs.toString())
        val error = result.error
        if (error != null && error.type == "SourceError" && error.kind == SourceErrorKind.RATE_LIMITED) {
            state = SessionState.LIMITED
            retryAfterSeconds = error.retryAfterSeconds
        } else {
            state = if (engine!!.broken == null) SessionState.IDLE else SessionState.BROKEN
        }
        return result
    }

    /**
     * Downloads the image of a contract ImageRequest (`{url, headers}`) with the source's cookies
     * and limiter. HTTP 429/509 puts the session into LIMITED like a RateLimited script result;
     * while limited nothing is requested.
     */
    suspend fun fetchImage(request: JsonElement): ByteArray {
        if (state == SessionState.LIMITED) throw SourceLimitedException(retryAfterSeconds)
        val obj = request.jsonObject
        val url = (obj["url"] as? JsonPrimitive)?.contentOrNull ?: throw IllegalArgumentException("ImageRequest without url")
        val headers = (obj["headers"] as? JsonObject)?.mapNotNull { (k, v) ->
            (v as? JsonPrimitive)?.takeIf { it.isString }?.let { k to it.content }
        }?.toMap()
        try {
            return http.fetchImage(url, headers)
        } catch (e: ImageRateLimitException) {
            state = SessionState.LIMITED
            retryAfterSeconds = e.retryAfterSeconds
            throw SourceLimitedException(e.retryAfterSeconds)
        }
    }

    /** The user explicitly retries after a rate limit. */
    fun clearLimit() {
        if (state == SessionState.LIMITED) state = SessionState.IDLE
        retryAfterSeconds = null
    }

    // --- HostBridge ---

    override suspend fun http(metaJson: String, body: ByteArray?): HttpBridgeResult = http.request(metaJson, body)
    override fun htmlSelect(markup: String, elementId: Int, selector: String) = html.select(markup, elementId, selector)
    override fun htmlCall(op: String, elementId: Int, arg: String) = html.call(op, elementId, arg)
    override suspend fun storageGet(key: String) = options.storage.get(key)
    override suspend fun storageSet(key: String, valueJson: String) = options.storage.set(key, valueJson)

    override suspend fun sleep(ms: Double): String {
        if (ms.isNaN() || ms < 0 || ms > 60_000) return Envelope.error(SourceErrorKind.INVALID_INPUT, "sleep: ms must be between 0 and 60000")
        if (options.enforcePoliteness) delay(ms.toLong())
        return Envelope.value(JsonNull)
    }

    override fun log(level: String, text: String) {
        options.onLog?.invoke(level, text)
    }

    override fun idle() = html.releaseHandles()
    override val fatal: Throwable? get() = fatalError

    override fun close() {
        engine?.close()
    }

    companion object {
        private val CONTRACT = Regex("^\\d+\\.\\d+$")

        /** Loads a source; throws [LoadException] if it cannot run on this host. */
        suspend fun open(script: String, name: String, options: SessionOptions): SourceSession {
            val session = SourceSession(script, name, options)
            try {
                session.startEngine()
            } catch (e: Throwable) {
                session.close()
                throw e
            }
            return session
        }

        fun isCompatible(hostVersion: String, required: String): Boolean {
            val h = hostVersion.split('.').map { it.toInt() }
            val r = required.split('.').map { it.toInt() }
            return r[0] == h[0] && r[1] <= h[1]
        }
    }
}

/** The source is rate-limited: nothing is requested until the user retries. */
class SourceLimitedException(val retryAfterSeconds: Double?) : Exception("the source is rate-limited; retry explicitly")
