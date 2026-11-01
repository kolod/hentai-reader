package hentaireader.host.sources

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

class SettingsException(message: String) : Exception(message)

/**
 * Settings resolution and the contract 1.1 calling convention (port of resolveSettings,
 * takesSettings and callArgs from tools/runner/src/source.ts and the Windows CallArgs).
 */
object CallArgs {
    private val ARITY = mapOf("search" to 2, "getGallery" to 1, "getPageUrl" to 3, "suggestTags" to 1, "getAccountStatus" to 0)
    private val MAJOR_MINOR = Regex("^(\\d+)\\.(\\d+)$")

    /** Manifest defaults merged with overrides; unknown ids or values throw. */
    fun resolveSettings(manifest: JsonObject, overrides: Map<String, String>? = null): JsonObject {
        val defs = (manifest["settings"] as? JsonArray)?.map { it.jsonObject } ?: emptyList()
        val result = LinkedHashMap<String, JsonElement>()
        for (d in defs) result[d["id"]!!.jsonPrimitive.content] = JsonPrimitive(d["default"]!!.jsonPrimitive.content)
        for ((id, value) in overrides.orEmpty()) {
            val def = defs.firstOrNull { it["id"]!!.jsonPrimitive.content == id } ?: throw SettingsException("unknown setting '$id'")
            if ((def["values"] as JsonArray).none { it.jsonPrimitive.content == value }) throw SettingsException("setting $id: '$value' is not allowed")
            result[id] = JsonPrimitive(value)
        }
        return JsonObject(result)
    }

    /** True when the source declares contract 1.1 or later. */
    fun takesSettings(manifest: JsonObject): Boolean {
        val m = MAJOR_MINOR.find((manifest["contract"] as? JsonPrimitive)?.contentOrNull ?: "") ?: return false
        return m.groupValues[1].toInt() > 1 || m.groupValues[2].toInt() >= 1
    }

    /** Arguments as the calling convention requires for this source's contract version. */
    fun build(manifest: JsonObject, fn: String, args: List<JsonElement?>, settings: JsonObject): JsonArray {
        val capabilities = manifest["capabilities"] as? JsonObject
        if (fn == "getAccountStatus" && (capabilities?.get("accountStatus") as? JsonPrimitive)?.booleanOrNull != true) {
            throw SettingsException("getAccountStatus is not declared in manifest.capabilities")
        }
        if (!takesSettings(manifest)) {
            val given = if (args.size > 2 && fn == "getPageUrl" && (args[2] == null || args[2] == JsonNull)) args.take(2) else args
            return JsonArray(given.map { it ?: JsonNull })
        }
        val arity = ARITY[fn] ?: args.size
        val result = ArrayList<JsonElement>()
        for (i in 0 until arity) result.add(args.getOrNull(i) ?: JsonNull)
        result.add(settings)
        return JsonArray(result)
    }
}
