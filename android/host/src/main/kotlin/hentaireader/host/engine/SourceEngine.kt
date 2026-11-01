package hentaireader.host.engine

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.util.concurrent.TimeUnit

/** Contract limits per source call (host-api.d.ts execution model). */
data class EngineLimits(val callTimeoutMs: Long, val memoryBytes: Long) {
    companion object {
        val DEFAULT = EngineLimits(60_000, 128L * 1024 * 1024)
    }
}

/** Outcome of a source call, classified as in the runner (contracts/host-bridge.md). */
data class CallResult(val ok: Boolean, val value: JsonElement?, val error: CallError?) {
    companion object {
        fun success(value: JsonElement?) = CallResult(true, value, null)
        fun failure(error: CallError) = CallResult(false, null, error)
    }
}

data class CallError(
    val type: String,
    val kind: String?,
    val name: String?,
    val message: String,
    val retryAfterSeconds: Double? = null,
    val details: JsonObject? = null,
)

data class SourceDescription(val manifest: JsonObject, val functions: List<String>)

class LoadException(message: String) : Exception(message)

/** contract/prelude.js and contract/host-glue.js, evaluated before every source. */
class ContractScripts(val prelude: String, val glue: String)

/**
 * One QuickJS runtime running one source module: contract prelude, the shared host glue
 * (contract/host-glue.js) on top of the QuickJS bridge adapter, and the source. All engine access
 * happens on its own thread; calls are serialised. Port of the Windows SourceEngine, with the
 * runner's error classification (same engine build).
 */
class SourceEngine private constructor(
    private val bridge: HostBridge,
    private val limits: EngineLimits,
    name: String,
) {
    private val thread = EngineThread("source-engine:$name")
    private val io = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val callLock = Mutex()
    private val finished = HashMap<Int, String>()
    private var ptr = 0L
    private var invoker = 0L
    private var settle = 0L
    private var settleHttp = 0L
    private var watch = 0L
    private var nextCallId = 0
    private var jobError: String? = null

    /** Set after a limit hit; the session replaces a broken engine. */
    @Volatile var broken: String? = null
        private set

    private val callbacks = object : QuickJsNative.Callbacks {
        override fun onNativeCall(index: Int, args: Array<Any?>): Any? = when (NATIVES[index]) {
            "http" -> {
                val id = id(args)
                val meta = args[1] as String
                val body = args.getOrNull(2) as? ByteArray
                start(id, { bridge.http(meta, body) }) { r -> call(settleHttp, id.toDouble(), r.env, r.bytes) }
                null
            }
            "htmlSelect" -> bridge.htmlSelect(args[0] as String, (args[1] as Double).toInt(), args[2] as String)
            "htmlCall" -> bridge.htmlCall(args[0] as String, (args[1] as Double).toInt(), args[2] as String)
            "storageGet" -> startString(id(args)) { bridge.storageGet(args[1] as String) }
            "storageSet" -> startString(id(args)) { bridge.storageSet(args[1] as String, args[2] as String) }
            "sleep" -> startString(id(args)) { bridge.sleep(args[1] as Double) }
            "log" -> {
                bridge.log(args[0].toString(), args[1].toString())
                null
            }
            "done" -> {
                finished[id(args)] = args[1] as String
                null
            }
            else -> null
        }
    }

    private fun id(args: Array<Any?>) = (args[0] as Double).toInt()

    /** Starts Kotlin work; its result is settled on the engine thread. */
    private fun <T> start(id: Int, work: suspend () -> T, settleWith: (T) -> Unit) {
        io.launch {
            val result = runCatching { work() }
            thread.post {
                // The engine was stopped (limit, disposal): nothing may run in it any more.
                if (broken != null || ptr == 0L) return@post
                result.fold(
                    onSuccess = settleWith,
                    onFailure = { e -> call(settle, id.toDouble(), Envelope.error(SourceErrorKind.NETWORK, e.message ?: "cancelled")) },
                )
                pump()
            }
        }
    }

    private fun startString(id: Int, work: suspend () -> String): Any? {
        start(id, work) { s -> call(settle, id.toDouble(), s) }
        return null
    }

    private fun call(fn: Long, vararg args: Any?) {
        val result = QuickJsNative.call(ptr, fn, arrayOf(*args))
        if (result != 0L) QuickJsNative.free(ptr, result)
    }

    private fun pump() {
        if (!QuickJsNative.executePendingJobs(ptr)) jobError = QuickJsNative.lastError(ptr)
    }

    private fun initialise(scripts: ContractScripts, source: String, sourceName: String, frozenNow: Long?) {
        ptr = QuickJsNative.create(limits.memoryBytes, callbacks, NATIVES)
        if (frozenNow != null) evalOrThrow("globalThis.__frozenNow = $frozenNow;", "frozen-now.js", free = true)
        val adapter = evalOrThrow(ADAPTER, "qjs-bridge.js")
        settle = QuickJsNative.getProperty(ptr, adapter, "settle")
        settleHttp = QuickJsNative.getProperty(ptr, adapter, "settleHttp")
        watch = QuickJsNative.getProperty(ptr, adapter, "watch")
        QuickJsNative.free(ptr, adapter)
        evalOrThrow(scripts.prelude, "prelude.js", free = true)
        val bind = evalOrThrow(scripts.glue, "host-glue.js")

        QuickJsNative.setTimeout(ptr, limits.callTimeoutMs)
        val exports = QuickJsNative.evalModule(ptr, source, sourceName)
        QuickJsNative.setTimeout(ptr, 0)
        if (exports == 0L) {
            val err = describe(QuickJsNative.lastError(ptr))
            throw LoadException("$sourceName: ${err.string("name") ?: "Error"}: ${err.string("message") ?: ""}")
        }
        invoker = QuickJsNative.call(ptr, bind, arrayOf(exports))
        QuickJsNative.free(ptr, exports)
        QuickJsNative.free(ptr, bind)
        if (invoker == 0L) throw LoadException("$sourceName: ${QuickJsNative.lastError(ptr)}")
    }

    private fun evalOrThrow(code: String, file: String, free: Boolean = false): Long {
        val h = QuickJsNative.evalGlobal(ptr, code, file)
        if (h == 0L) {
            val err = describe(QuickJsNative.lastError(ptr))
            throw LoadException("$file: ${err.string("name") ?: "Error"}: ${err.string("message") ?: ""}")
        }
        if (free) {
            QuickJsNative.free(ptr, h)
            return 0L
        }
        return h
    }

    suspend fun describe(): SourceDescription {
        val res = call("__describe", "[]")
        if (!res.ok) throw LoadException("cannot read source exports: ${res.error!!.message}")
        val value = res.value!!.jsonObject
        val manifest = value["manifest"] as? JsonObject ?: throw LoadException("source does not export `manifest`")
        val functions = value["functions"]!!.jsonArray.map { it.jsonPrimitive.content }
        return SourceDescription(manifest, functions)
    }

    /**
     * Calls an exported function with JSON arguments. Never throws for script errors. Cancelling
     * the caller only stops waiting for the engine lock: a call that already runs finishes within
     * the time limit (stopping a script means discarding the whole engine and its state, e.g.
     * Hitomi's gg.js), and the lock is held until it does, so calls never nest on the thread.
     */
    suspend fun call(fn: String, argsJson: String): CallResult {
        broken?.let { return limit("EngineBroken", it, "time") }
        return callLock.withLock {
            withContext(NonCancellable) { thread.invoke { runCall(fn, argsJson) } }
        }
    }

    private fun runCall(fn: String, argsJson: String): CallResult {
        val id = ++nextCallId
        jobError = null
        QuickJsNative.setTimeout(ptr, limits.callTimeoutMs)
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(limits.callTimeoutMs)
        try {
            val promise = QuickJsNative.call(ptr, invoker, arrayOf(fn, argsJson))
            if (promise == 0L) return raw(QuickJsNative.lastError(ptr))
            call(watch, promise, id.toDouble())
            QuickJsNative.free(ptr, promise)
            pump()
            thread.pumpUntil(deadline) { finished.containsKey(id) || bridge.fatal != null || jobError != null }
            bridge.fatal?.let { throw it }
            jobError?.let { return raw(it) }
            return parse(finished.remove(id)!!)
        } catch (_: EngineTimeoutException) {
            broken = "call exceeded the time limit"
            return limit("InternalError", "call exceeded the time limit", "time")
        } finally {
            QuickJsNative.setTimeout(ptr, 0)
            bridge.idle()
        }
    }

    /** An error raised by the VM itself (not wrapped by the glue): limits, uncaught throws. */
    private fun raw(errorJson: String?): CallResult {
        val result = classify(describe(errorJson))
        if (result.error?.details?.get("limit") != null) broken = result.error.message
        return result
    }

    private fun parse(json: String): CallResult {
        val node = Json.parseToJsonElement(json).jsonObject
        if (node["ok"]?.jsonPrimitive?.booleanOrNull == true) return CallResult.success(node["value"])
        val result = classify(node["error"]!!.jsonObject)
        if (result.error?.details?.get("limit") != null) broken = result.error.message
        return result
    }

    fun close() {
        if (ptr != 0L) QuickJsNative.cancel(ptr)
        io.cancel()
        thread.post {
            if (ptr != 0L) {
                QuickJsNative.destroy(ptr)
                ptr = 0L
            }
            thread.close()
        }
    }

    companion object {
        private val NATIVES = arrayOf("http", "htmlSelect", "htmlCall", "storageGet", "storageSet", "sleep", "log", "done")
        private val ADAPTER: String = SourceEngine::class.java.getResource("/hentaireader/host/engine/qjs-bridge.js")!!.readText()
        private val NOT_DEFINED = Regex("^'?([\\w$]+)'? is not defined$")
        private val OUT_OF_MEMORY = Regex("out of memory")

        suspend fun create(
            source: String,
            sourceName: String,
            bridge: HostBridge,
            scripts: ContractScripts,
            frozenNow: Long? = null,
            limits: EngineLimits = EngineLimits.DEFAULT,
        ): SourceEngine {
            val engine = SourceEngine(bridge, limits, sourceName)
            try {
                engine.thread.invoke { engine.initialise(scripts, source, sourceName, frozenNow) }
            } catch (e: Throwable) {
                engine.close()
                throw e as? LoadException ?: LoadException("$sourceName: ${e.message}")
            }
            return engine
        }

        private fun describe(json: String?): JsonObject =
            json?.let { runCatching { Json.parseToJsonElement(it).jsonObject }.getOrNull() }
                ?: buildJsonObject { put("name", JsonPrimitive("Thrown")); put("message", JsonPrimitive(json ?: "")) }

        private fun JsonObject.string(key: String): String? = (this[key] as? JsonPrimitive)?.contentOrNull

        private fun limit(name: String, message: String, limit: String) = CallResult.failure(
            CallError("ScriptError", null, name, message, null, buildJsonObject { put("limit", JsonPrimitive(limit)) }),
        )

        /** Maps errors described by the glue or the VM to contract-level classifications (runner's classify). */
        internal fun classify(err: JsonObject): CallResult {
            val type = err.string("type")
            val message = err.string("message") ?: ""
            val kind = err.string("kind")
            if (type == "SourceError" && kind != null) {
                return CallResult.failure(
                    CallError(
                        "SourceError", kind, null, message,
                        (err["retryAfterSeconds"] as? JsonPrimitive)?.doubleOrNull,
                        err["details"] as? JsonObject,
                    ),
                )
            }
            val name = err.string("name") ?: "Error"
            val missing = if (name == "ReferenceError") NOT_DEFINED.find(message) else null
            if (missing != null) {
                val capability = missing.groupValues[1]
                return CallResult.failure(
                    CallError("ScriptError", null, name, "forbidden capability: $capability", null,
                        buildJsonObject { put("capability", JsonPrimitive(capability)) }),
                )
            }
            if (name == "InternalError" && message == "interrupted") return limit(name, "call exceeded the time limit", "time")
            if (name == "InternalError" && OUT_OF_MEMORY.containsMatchIn(message)) return limit(name, "engine memory limit exceeded", "memory")
            return CallResult.failure(CallError("ScriptError", null, name, message))
        }
    }
}
