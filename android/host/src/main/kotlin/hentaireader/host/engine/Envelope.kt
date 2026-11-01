package hentaireader.host.engine

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject

/** Error kinds of the contract (host-api.d.ts SourceErrorKind). */
object SourceErrorKind {
    const val RATE_LIMITED = "RateLimited"
    const val LOGIN_REQUIRED = "LoginRequired"
    const val NOT_FOUND = "NotFound"
    const val SITE_CHANGED = "SiteChanged"
    const val INVALID_INPUT = "InvalidInput"
    const val NETWORK = "Network"
}

/**
 * JSON envelopes exchanged with contract/host-glue.js: `{"value": …}` or
 * `{"error": {"kind", "message", "options"}}` (specs/003-windows-host/contracts/host-bridge.md).
 */
object Envelope {
    fun value(value: JsonElement): String = buildJsonObject { put("value", value) }.toString()

    fun value(value: String?): String = value(if (value == null) JsonNull else JsonPrimitive(value))

    fun error(kind: String, message: String, options: JsonObject? = null): String = buildJsonObject {
        put("error", buildJsonObject {
            put("kind", JsonPrimitive(kind))
            put("message", JsonPrimitive(message))
            if (options != null) put("options", options)
        })
    }.toString()
}

/** Converts plain Kotlin values (maps, lists, strings, numbers, booleans, null) to JSON. */
fun toJson(value: Any?): JsonElement = when (value) {
    null -> JsonNull
    is JsonElement -> value
    is String -> JsonPrimitive(value)
    is Number -> JsonPrimitive(value)
    is Boolean -> JsonPrimitive(value)
    is Map<*, *> -> JsonObject(value.entries.associate { (k, v) -> k.toString() to toJson(v) })
    is Iterable<*> -> JsonArray(value.map { toJson(it) })
    else -> error("cannot convert ${value::class} to JSON")
}

/** Lenient JSON for host data (never for script output, which is parsed as-is). */
val HostJson = Json { ignoreUnknownKeys = true }
