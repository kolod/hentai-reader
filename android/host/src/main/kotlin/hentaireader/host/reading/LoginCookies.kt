package hentaireader.host.reading

import hentaireader.host.http.CookieJar
import hentaireader.host.sources.ManifestRules
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Which cookies of a sign-in a host keeps (contract 1.2 `manifest.loginPage`; port of the Windows
 * LoginCookies): only cookies whose domain is within the source's domains; the account is shared,
 * so the sign-in cookies found on any of them are also stored for the login page's domain and every
 * `shareWith` domain. Also parses cookies the user pastes. Values are never logged.
 */
object LoginCookies {
    /** Sign-in cookie names from loginPage.cookies that are not among [cookies]. */
    fun missing(loginPage: JsonObject, cookies: Iterable<CookieJar.Cookie>): List<String> {
        val present = cookies.filter { it.value.isNotEmpty() }.map { it.name }.toSet()
        return required(loginPage).filter { it !in present }
    }

    /** The cookies to store for the source: within its domains; sign-in cookies for every account domain. */
    fun select(manifest: JsonObject, captured: Iterable<CookieJar.Cookie>): List<CookieJar.Cookie> {
        val domains = (manifest["domains"] as? JsonArray).orEmpty().map { it.jsonPrimitive.content }
        val loginPage = manifest["loginPage"] as? JsonObject
        val kept = captured.map { it.copy(domain = it.domain.trimStart('.').lowercase()) }
            .filter { ManifestRules.hostInDomains(it.domain, domains) }
            .toMutableList()
        if (loginPage == null) return kept

        val required = required(loginPage)
        val account = kept.filter { it.name in required }.distinctBy { it.name }
        // Account domains: the login page's host and every shareWith domain.
        val accountDomains = (loginPage["shareWith"] as? JsonArray).orEmpty().map { it.jsonPrimitive.content.trimStart('.').lowercase() }.toMutableList()
        (loginPage["url"] as? JsonPrimitive)?.contentOrNull?.toHttpUrlOrNull()?.let { accountDomains.add(0, it.host.lowercase()) }
        for (domain in accountDomains.distinct().filter { ManifestRules.hostInDomains(it, domains) }) {
            kept.removeAll { it.domain == domain && it.name in required }
            kept.addAll(account.map { it.copy(domain = domain, hostOnly = false, path = "/") })
        }
        return kept
    }

    /**
     * Cookies pasted as a Netscape cookies.txt or as `name=value; name=value` (then they belong to
     * [defaultDomain] and its subdomains).
     */
    fun parse(text: String, defaultDomain: String): List<CookieJar.Cookie> {
        val normalized = text.replace("\r\n", "\n").replace('\r', '\n')
        val lines = normalized.split('\n')
        val result = mutableListOf<CookieJar.Cookie>()
        if (lines.any { it.split('\t').size >= 7 }) {
            for (raw in lines) {
                val line = raw.removePrefix("#HttpOnly_")
                if (line.isEmpty() || line.startsWith('#')) continue
                val f = line.split('\t')
                if (f.size < 7 || f[5].isEmpty()) continue
                val expiry = f[4].toLongOrNull() ?: 0
                result.add(CookieJar.Cookie(f[5], f[6].trim(), f[0].trimStart('.').lowercase(), !f[1].equals("TRUE", true),
                    f[2].ifEmpty { "/" }, if (expiry > 0) expiry * 1000 else null))
            }
            return result
        }
        for (part in normalized.replace("\n", ";").split(';')) {
            val eq = part.indexOf('=')
            if (eq <= 0) continue
            val name = part.substring(0, eq).trim()
            val value = part.substring(eq + 1).trim()
            if (name.isNotEmpty() && value.isNotEmpty()) result.add(CookieJar.Cookie(name, value, defaultDomain.trimStart('.').lowercase(), false, "/", null))
        }
        return result
    }

    private fun required(loginPage: JsonObject) = (loginPage["cookies"] as? JsonArray).orEmpty().map { it.jsonPrimitive.content }.toSet()
}
