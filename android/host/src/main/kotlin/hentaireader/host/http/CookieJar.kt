package hentaireader.host.http

import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Per-source cookie jar: Set-Cookie parsing, domain/path matching, Netscape import, script-header
 * merge (port of tools/runner/src/host/cookies.ts and the Windows CookieJar).
 */
class CookieJar {
    @Serializable
    data class Cookie(
        val name: String,
        val value: String,
        val domain: String,
        val hostOnly: Boolean,
        val path: String,
        val expires: Long?,
    )

    private val lock = Any()
    private var cookies = mutableListOf<Cookie>()

    /** Called after any change, so a persistent store can save the jar. */
    var onChanged: (() -> Unit)? = null

    fun store(url: String, setCookieHeaders: Iterable<String>) {
        val host = hostOf(url)
        var changed = false
        for (header in setCookieHeaders) {
            val parts = header.split(';')
            val eq = parts[0].indexOf('=')
            if (eq <= 0) continue
            var cookie = Cookie(parts[0].substring(0, eq).trim(), parts[0].substring(eq + 1).trim(), host, true, "/", null)
            for (attr in parts.drop(1)) {
                val kv = attr.split('=', limit = 2)
                val key = kv[0].trim().lowercase()
                val value = if (kv.size > 1) kv[1].trim() else ""
                when {
                    key == "domain" && value.isNotEmpty() -> {
                        val domain = value.trimStart('.').lowercase()
                        if (!domainMatches(host, domain)) continue
                        cookie = cookie.copy(domain = domain, hostOnly = false)
                    }
                    key == "path" && value.startsWith('/') -> cookie = cookie.copy(path = value)
                    key == "max-age" && value.toDoubleOrNull() != null ->
                        cookie = cookie.copy(expires = System.currentTimeMillis() + (value.toDouble() * 1000).toLong())
                    key == "expires" && cookie.expires == null -> parseDate(value)?.let { cookie = cookie.copy(expires = it) }
                }
            }
            put(cookie)
            changed = true
        }
        if (changed) onChanged?.invoke()
    }

    /** Seeds "k=v; k2=v2" for a domain and its subdomains. */
    fun seed(domain: String, cookieString: String) {
        for (part in cookieString.split(';')) {
            val eq = part.indexOf('=')
            if (eq <= 0) continue
            put(Cookie(part.substring(0, eq).trim(), part.substring(eq + 1).trim(), domain.trimStart('.').lowercase(), false, "/", null))
        }
        onChanged?.invoke()
    }

    /** Seeds from a Netscape cookies.txt. */
    fun seedNetscape(text: String) {
        for (raw in text.split('\n')) {
            var line = raw.trimEnd('\r')
            if (line.startsWith("#HttpOnly_")) line = line.removePrefix("#HttpOnly_")
            if (line.isEmpty() || line.startsWith('#')) continue
            val f = line.split('\t')
            if (f.size < 7) continue
            val expiry = f[4].toLongOrNull() ?: 0
            put(Cookie(f[5], f[6], f[0].trimStart('.').lowercase(), !f[1].equals("TRUE", ignoreCase = true),
                f[2].ifEmpty { "/" }, if (expiry > 0) expiry * 1000 else null))
        }
        onChanged?.invoke()
    }

    /** Cookies applicable to a URL, name → value. */
    fun forUrl(url: String): Map<String, String> {
        val parsed = url.toHttpUrlOrNull() ?: return emptyMap()
        val host = parsed.host.lowercase()
        val path = parsed.encodedPath
        val now = System.currentTimeMillis()
        val result = LinkedHashMap<String, String>()
        synchronized(lock) {
            for (c in cookies) {
                if (c.expires != null && c.expires <= now) continue
                val hostOk = if (c.hostOnly) host == c.domain else domainMatches(host, c.domain)
                if (hostOk && path.startsWith(c.path)) result[c.name] = c.value
            }
        }
        return result
    }

    fun serialize(): String = synchronized(lock) { Json.encodeToString(ListSerializer(Cookie.serializer()), cookies) }

    fun load(json: String) {
        val list = Json.decodeFromString(ListSerializer(Cookie.serializer()), json)
        synchronized(lock) { cookies = list.toMutableList() }
    }

    /** Adds cookies obtained outside a response (sign-in in an embedded browser, pasted by the user). */
    fun add(added: Iterable<Cookie>) {
        for (cookie in added) put(cookie.copy(domain = cookie.domain.trimStart('.').lowercase()))
        onChanged?.invoke()
    }

    /** Forgets every cookie (sign-out). */
    fun clear() {
        synchronized(lock) { cookies = mutableListOf() }
        onChanged?.invoke()
    }

    /** Names of the cookies held for a domain (never values; for status checks and tests). */
    fun namesFor(domain: String): List<String> {
        val d = domain.trimStart('.').lowercase()
        return synchronized(lock) { cookies.filter { it.domain == d }.map { it.name } }
    }

    private fun put(cookie: Cookie) = synchronized(lock) {
        cookies.removeAll { it.name == cookie.name && it.domain == cookie.domain && it.path == cookie.path }
        cookies.add(cookie)
    }

    companion object {
        fun domainMatches(host: String, domain: String) = host == domain || host.endsWith(".$domain")

        /** Merges jar cookies with a script Cookie header; script values win on name clashes. */
        fun mergeCookieHeader(jar: Map<String, String>, scriptHeader: String?): String? {
            val merged = LinkedHashMap(jar)
            if (!scriptHeader.isNullOrEmpty()) {
                for (part in scriptHeader.split(';')) {
                    val eq = part.indexOf('=')
                    if (eq <= 0) continue
                    merged[part.substring(0, eq).trim()] = part.substring(eq + 1).trim()
                }
            }
            return if (merged.isEmpty()) null else merged.entries.joinToString("; ") { "${it.key}=${it.value}" }
        }

        private fun hostOf(url: String) = url.toHttpUrlOrNull()?.host?.lowercase() ?: ""

        private fun parseDate(value: String): Long? =
            runCatching { ZonedDateTime.parse(value, DateTimeFormatter.RFC_1123_DATE_TIME).toInstant().toEpochMilli() }.getOrNull()
                ?: runCatching { ZonedDateTime.parse(value.replace('-', ' '), DateTimeFormatter.ofPattern("EEE, dd MMM yyyy HH:mm:ss zzz", java.util.Locale.US)).toInstant().toEpochMilli() }.getOrNull()
    }
}
