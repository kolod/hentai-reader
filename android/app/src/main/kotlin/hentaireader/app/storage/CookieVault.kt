package hentaireader.app.storage

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json

/**
 * Cookies of every source, stored only on the device (007 FR-036, research R13): one file
 * (`cookies.bin` in the no-backup folder) encrypted with AES-GCM under a key that lives in the
 * Android Keystore and never leaves it. Each source's jar is kept as its serialized JSON; values
 * are never logged.
 */
class CookieVault(private val file: File) {
    private val lock = Any()
    private var jars: MutableMap<String, String>? = null

    /** The serialized jar of a source, or null. */
    fun load(sourceId: String): String? = synchronized(lock) { all()[sourceId] }

    fun save(sourceId: String, jarJson: String) = synchronized(lock) {
        all()[sourceId] = jarJson
        write()
    }

    private fun all(): MutableMap<String, String> = jars ?: read().toMutableMap().also { jars = it }

    private fun read(): Map<String, String> = runCatching {
        val bytes = file.readBytes()
        val iv = bytes.copyOfRange(0, IV_BYTES)
        val cipher = Cipher.getInstance(TRANSFORM).apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv)) }
        Json.decodeFromString(SERIALIZER, String(cipher.doFinal(bytes, IV_BYTES, bytes.size - IV_BYTES), Charsets.UTF_8))
    }.getOrDefault(emptyMap())

    private fun write() {
        val cipher = Cipher.getInstance(TRANSFORM).apply { init(Cipher.ENCRYPT_MODE, key()) }
        val data = cipher.doFinal(Json.encodeToString(SERIALIZER, jars!!).toByteArray(Charsets.UTF_8))
        file.parentFile?.mkdirs()
        val temp = File(file.path + ".tmp")
        temp.writeBytes(cipher.iv + data)
        if (!temp.renameTo(file)) {
            file.delete()
            temp.renameTo(file)
        }
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build())
        }.generateKey()
    }

    private companion object {
        const val ALIAS = "hr_cookies"
        const val TRANSFORM = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
        val SERIALIZER = MapSerializer(String.serializer(), String.serializer())
    }
}
