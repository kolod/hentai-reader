package hentaireader.host.fixtures

import hentaireader.host.http.RawRequest
import hentaireader.host.http.RawResponse
import hentaireader.host.http.Transport
import hentaireader.host.http.UnmatchedRequestException
import java.io.File
import java.security.MessageDigest
import java.time.OffsetDateTime
import kotlinx.coroutines.delay
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * Answers requests only from a recorded fixture pool (port of tools/runner/src/transport/offline.ts
 * and the Windows OfflineTransport): key = method + URL + range header + SHA-256 of the body;
 * supports echo and delayMs.
 */
class OfflineTransport(private val dir: File) : Transport {
    private val entries = HashMap<String, JsonObject>()

    val recordedAt: Long

    init {
        val index = File(dir, "index.json")
        if (!index.exists()) {
            recordedAt = 0
        } else {
            val root = Json.parseToJsonElement(index.readText()).jsonObject
            recordedAt = OffsetDateTime.parse(root["recordedAt"]!!.jsonPrimitive.content).toInstant().toEpochMilli()
            for (e in root["entries"]!!.jsonArray) {
                val entry = e.jsonObject
                val req = entry["request"]!!.jsonObject
                val range = (req["headers"] as? JsonObject)?.get("range")?.jsonPrimitive?.contentOrNull
                entries[key(req["method"]!!.jsonPrimitive.content, req["url"]!!.jsonPrimitive.content, range,
                    req["bodySha256"]?.jsonPrimitive?.contentOrNull)] = entry
            }
        }
    }

    override suspend fun send(request: RawRequest): RawResponse {
        val bodySha = request.body?.let { sha256Hex(it) }
        val range = request.headers["range"]
        val entry = entries[key(request.method, request.url, range, bodySha)]
            ?: throw UnmatchedRequestException(request.method, request.url, range)
        val response = entry["response"]!!.jsonObject
        val delayMs = (response["delayMs"] as? JsonPrimitive)?.doubleOrNull ?: 0.0
        if (delayMs > 0) delay(delayMs.toLong())

        if ((response["echo"] as? JsonPrimitive)?.booleanOrNull == true) {
            val json = JsonObject(mapOf(
                "method" to JsonPrimitive(request.method),
                "url" to JsonPrimitive(request.url),
                "headers" to JsonObject(request.headers.mapValues { JsonPrimitive(it.value) }),
                "bodySha256" to JsonPrimitive(bodySha),
            )).toString()
            return RawResponse(200, listOf("content-type" to "application/json"), json.toByteArray(Charsets.UTF_8))
        }
        val headers = (response["headers"] as? JsonObject)?.map { (k, v) -> k.lowercase() to v.jsonPrimitive.content } ?: emptyList()
        val bodyPath = response["body"]?.jsonPrimitive?.contentOrNull
        val body = if (bodyPath == null) ByteArray(0) else File(dir, bodyPath).readBytes()
        return RawResponse(response["status"]!!.jsonPrimitive.int, headers, body)
    }

    companion object {
        fun key(method: String, url: String, range: String?, bodySha256: String?) =
            listOf(method.uppercase(), url, range ?: "", bodySha256 ?: "").joinToString("\n")

        fun sha256Hex(bytes: ByteArray): String =
            MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    }
}
