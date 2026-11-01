package hentaireader.host.reading

import hentaireader.host.http.ImageHttpException
import hentaireader.host.http.RawRequest
import hentaireader.host.http.RawResponse
import hentaireader.host.http.Transport
import hentaireader.host.parity.Repo
import hentaireader.host.sources.SessionOptions
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.SourceStorage
import java.io.File
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/**
 * A stalled or failing image server: the reader asks the source for another location (port of the
 * Windows PageImageLoaderTests, with the real E-Hentai script and its `?nl=` retry path).
 */
class PageImageLoaderTest {
    private val viewer = "https://e-hentai.org/s/0123456789/1234567-1"

    private inner class Site : Transport {
        val stalled = HashSet<String>()
        val failing = HashSet<String>()
        val requests = mutableListOf<String>()

        override suspend fun send(request: RawRequest): RawResponse {
            synchronized(requests) { requests.add(request.url) }
            if (request.url.startsWith(viewer)) {
                val nl = request.url.substringAfter("?nl=", "").ifEmpty { null }
                val server = when (nl) { null -> "a"; "a-token" -> "b"; "b-token" -> "c"; else -> "z" }
                return html("""
                    <div id="i2"><div>001.webp :: 1280 x 1810 :: 210.4 KiB</div></div>
                    <img id="img" src="https://$server.hath.network/h/abc/001.webp" />
                    <a href="#" id="loadfail" onclick="return nl('$server-token')">Reload broken image</a>
                """.trimIndent())
            }
            if (request.url in stalled) awaitCancellation()
            if (request.url in failing) return RawResponse(503, emptyList(), ByteArray(0))
            return RawResponse(200, listOf("content-type" to "image/webp"), request.url.toByteArray())
        }

        private fun html(body: String) = RawResponse(200, listOf("content-type" to "text/html; charset=utf-8"), body.toByteArray())
    }

    private val gallery: JsonObject = Json.parseToJsonElement("""{"id":"e/1234567/0123456789","sourceData":{"pages":[["0123456789",1]]}}""").jsonObject

    private fun open(site: Site) = runBlocking {
        val script = File(Repo.sources, "ehentai/ehentai.js").readText()
        SourceSession.open(script, "ehentai.js", SessionOptions(site, false, SourceStorage(), Repo.scripts))
    }

    @Test
    fun `a stalled image is loaded from the new location on the second attempt`() = runBlocking {
        val site = Site().apply { stalled.add("https://a.hath.network/h/abc/001.webp") }
        open(site).use { session ->
            val page = PageAttempts()
            val resolved = mutableListOf<String>()
            val bytes = PageImageLoader(session, timeoutMs = 300).load(gallery, 0, page) { resolved.add(it.jsonObject["url"]!!.jsonPrimitive.content) }
            assertEquals("https://b.hath.network/h/abc/001.webp", String(bytes))
            assertEquals(listOf("https://a.hath.network/h/abc/001.webp", "https://b.hath.network/h/abc/001.webp"), resolved)
            assertTrue("$viewer?nl=a-token" in site.requests)
            assertEquals(1, page.attempts)
            assertNull(page.failedStatus)
        }
    }

    @Test
    fun `a page fails with a timeout after the automatic retries`() {
        val site = Site().apply { listOf("a", "b", "c").forEach { stalled.add("https://$it.hath.network/h/abc/001.webp") } }
        open(site).use { session ->
            val page = PageAttempts()
            assertThrows<PageTimeoutException> { runBlocking { PageImageLoader(session, timeoutMs = 200, autoRetries = 2).load(gallery, 0, page, null) } }
            assertEquals(3, site.requests.count { ".hath.network/" in it })
            assertEquals(2, page.attempts)
            assertEquals("c-token", page.pageImage!!.jsonObject["retryToken"]!!.jsonPrimitive.content)
        }
    }

    @Test
    fun `a server error is retried at a new location with the status`() = runBlocking {
        val site = Site().apply { failing.add("https://a.hath.network/h/abc/001.webp") }
        open(site).use { session ->
            val page = PageAttempts()
            val bytes = PageImageLoader(session, retryDelayMs = 10).load(gallery, 0, page, null)
            assertEquals("https://b.hath.network/h/abc/001.webp", String(bytes))
            assertEquals(503, page.failedStatus)
            assertTrue("$viewer?nl=a-token" in site.requests)
        }
    }

    @Test
    fun `server errors everywhere fail after the retries`() {
        val site = Site().apply { listOf("a", "b", "c").forEach { failing.add("https://$it.hath.network/h/abc/001.webp") } }
        open(site).use { session ->
            val e = assertThrows<ImageHttpException> { runBlocking { PageImageLoader(session, retryDelayMs = 10).load(gallery, 0, PageAttempts(), null) } }
            assertEquals(503, e.status)
            assertEquals(3, site.requests.count { ".hath.network/" in it })
        }
    }

    @Test
    fun `a fast image needs no retry hint`() = runBlocking {
        val site = Site()
        open(site).use { session ->
            val page = PageAttempts()
            assertEquals("https://a.hath.network/h/abc/001.webp", String(PageImageLoader(session).load(gallery, 0, page, null)))
            assertTrue(site.requests.none { "?nl=" in it })
            assertEquals(0, page.attempts)
            assertNull(PageImageLoader.retryHint(page))
        }
    }
}
