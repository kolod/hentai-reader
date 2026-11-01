package hentaireader.host.sources

import hentaireader.host.http.CookieJar
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * manifest.loginPage rules of contract 1.2 (specs/005-windows-reader/contracts/contract-1.2-login-page.md),
 * the same rules and texts as tools/runner/src/login-page.ts and the Windows host. A source that
 * breaks them does not load.
 */
object ManifestRules {
    /** True when [host] is allowed by manifest domains ("site.org" or "*.site.org"). */
    fun hostInDomains(host: String, domains: Iterable<String>): Boolean {
        val h = host.lowercase()
        return domains.any { d ->
            val pattern = d.lowercase()
            if (pattern.startsWith("*.")) CookieJar.domainMatches(h, pattern.substring(2)) && h != pattern.substring(2)
            else h == pattern
        }
    }

    fun loginPageViolations(manifest: JsonObject): List<String> {
        val violations = mutableListOf<String>()
        val page = manifest["loginPage"] as? JsonObject ?: return violations
        val domains = (manifest["domains"] as? JsonArray)?.map { (it as? JsonPrimitive)?.contentOrNull ?: "" } ?: emptyList()

        val contract = (manifest["contract"] as? JsonPrimitive)?.contentOrNull?.split('.')
        val major = contract?.getOrNull(0)?.toIntOrNull()
        val minor = contract?.getOrNull(1)?.toIntOrNull()
        if (contract?.size != 2 || major == null || minor == null || (major == 1 && minor < 2)) violations.add("manifest.loginPage: requires contract 1.2")
        if ((manifest["login"] as? JsonPrimitive)?.contentOrNull == "none") violations.add("manifest.loginPage: not allowed when login is \"none\"")

        val urlText = (page["url"] as? JsonPrimitive)?.takeIf { it.isString }?.content
        val url = urlText?.toHttpUrlOrNull()
        if (url == null || url.scheme != "https") violations.add("manifest.loginPage.url: must be an absolute https URL")
        else if (!hostInDomains(url.host, domains)) violations.add("manifest.loginPage.url: host ${url.host} is outside manifest.domains")

        val cookies = (page["cookies"] as? JsonArray)?.map { (it as? JsonPrimitive)?.takeIf { p -> p.isString }?.content ?: "" } ?: emptyList()
        if (cookies.isEmpty() || cookies.any { it.isEmpty() } || cookies.distinct().size != cookies.size) {
            violations.add("manifest.loginPage.cookies: must list at least one cookie name, each once")
        }

        for (share in (page["shareWith"] as? JsonArray) ?: JsonArray(emptyList())) {
            val domain = (share as? JsonPrimitive)?.takeIf { it.isString }?.content ?: ""
            if (!hostInDomains(domain, domains)) violations.add("manifest.loginPage.shareWith: $domain is outside manifest.domains")
        }
        return violations
    }
}
