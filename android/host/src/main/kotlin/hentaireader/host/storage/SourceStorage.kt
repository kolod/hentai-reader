package hentaireader.host.storage

import hentaireader.host.engine.Envelope
import hentaireader.host.engine.SourceErrorKind
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonPrimitive

/**
 * Per-source key-value storage with the contract limits; in memory, optionally persisted to a
 * JSON file (atomic replace) so it survives restarts (port of the Windows SourceStorage).
 */
class SourceStorage(private val file: File? = null) {
    private val data = LinkedHashMap<String, String>()
    private val lock = Any()

    init {
        val stored = file?.takeIf { it.exists() }
        if (stored != null) {
            runCatching { Json.decodeFromString(SERIALIZER, stored.readText()) }.getOrNull()?.let { data.putAll(it) }
        }
    }

    fun seed(values: Map<String, String>) = synchronized(lock) { data.putAll(values) }

    suspend fun get(key: String): String {
        checkKey(key)?.let { return it }
        return synchronized(lock) { Envelope.value(data[key]) }
    }

    suspend fun set(key: String, valueJson: String): String {
        checkKey(key)?.let { return it }
        val parsed = Json.parseToJsonElement(valueJson)
        val value = if (parsed == JsonNull) null else (parsed as JsonPrimitive).content
        val snapshot: String?
        synchronized(lock) {
            if (value == null) {
                data.remove(key)
            } else {
                val size = value.toByteArray(Charsets.UTF_8).size
                if (size > VALUE_BYTES) return Envelope.error(SourceErrorKind.INVALID_INPUT, "storage.set: value for '$key' exceeds $VALUE_BYTES bytes")
                val total = size + key.toByteArray(Charsets.UTF_8).size +
                    data.filterKeys { it != key }.entries.sumOf { it.key.toByteArray(Charsets.UTF_8).size + it.value.toByteArray(Charsets.UTF_8).size }
                if (total > TOTAL_BYTES) return Envelope.error(SourceErrorKind.INVALID_INPUT, "storage.set: source storage exceeds $TOTAL_BYTES bytes")
                data[key] = value
            }
            snapshot = file?.let { Json.encodeToString(SERIALIZER, data) }
        }
        if (snapshot != null) writeAtomic(file!!, snapshot)
        return Envelope.value(JsonNull)
    }

    private fun checkKey(key: String): String? =
        if (key.isEmpty() || key.length > KEY_CHARS) Envelope.error(SourceErrorKind.INVALID_INPUT, "storage: key must be 1–$KEY_CHARS characters") else null

    companion object {
        const val KEY_CHARS = 128
        const val VALUE_BYTES = 1024 * 1024
        const val TOTAL_BYTES = 8 * 1024 * 1024
        private val SERIALIZER = MapSerializer(String.serializer(), String.serializer())

        suspend fun writeAtomic(file: File, text: String) = withContext(Dispatchers.IO) {
            file.parentFile?.mkdirs()
            val temp = File(file.path + ".tmp")
            temp.writeText(text)
            Files.move(temp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
        }
    }
}
