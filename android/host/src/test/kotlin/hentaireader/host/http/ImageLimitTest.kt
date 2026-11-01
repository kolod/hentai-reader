package hentaireader.host.http

import hentaireader.host.parity.Repo
import hentaireader.host.sources.SessionOptions
import hentaireader.host.sources.SessionState
import hentaireader.host.sources.SourceLimitedException
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.SourceStorage
import java.io.File
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import org.junit.jupiter.api.Test

/** HTTP 429/509 on an image stops the source until the user retries (port of the Windows ImageLimitTests). */
class ImageLimitTest {
    private class FakeTransport(var status: Int = 200) : Transport {
        val headers = mutableListOf<Pair<String, String>>()
        var requests = 0

        override suspend fun send(request: RawRequest): RawResponse {
            requests++
            return RawResponse(status, headers.toList(), byteArrayOf(1, 2, 3))
        }
    }

    private fun open(transport: FakeTransport): SourceSession = runBlocking {
        val script = File(Repo.conformance, "minimal/minimal.js").readText()
        SourceSession.open(script, "minimal.js", SessionOptions(transport, false, SourceStorage(), Repo.scripts))
    }

    private fun image(url: String): JsonElement = Json.parseToJsonElement("""{"url":"$url","headers":{"referer":"https://example.test/"}}""")

    @ParameterizedTest
    @ValueSource(ints = [429, 509])
    fun `a limit status puts the session into LIMITED`(status: Int) {
        val transport = FakeTransport(status).apply { headers.add("retry-after" to "120") }
        open(transport).use { session ->
            val e = assertThrows<SourceLimitedException> { runBlocking { session.fetchImage(image("https://example.test/p/1.webp")) } }
            assertEquals(120.0, e.retryAfterSeconds)
            assertEquals(SessionState.LIMITED, session.state)
        }
    }

    @Test
    fun `nothing is requested while limited until clearLimit`() = runBlocking {
        val transport = FakeTransport(509)
        open(transport).use { session ->
            assertThrows<SourceLimitedException> { runBlocking { session.fetchImage(image("https://example.test/p/1.webp")) } }
            val sent = transport.requests
            transport.status = 200
            assertThrows<SourceLimitedException> { runBlocking { session.fetchImage(image("https://example.test/p/2.webp")) } }
            val call = session.call("search", listOf(Json.parseToJsonElement("""{"text":"","filters":{}}"""), null))
            assertFalse(call.ok)
            assertEquals(sent, transport.requests)

            session.clearLimit()
            assertArrayEquals(byteArrayOf(1, 2, 3), session.fetchImage(image("https://example.test/p/2.webp")))
        }
    }

    @Test
    fun `other errors do not limit the source`() {
        open(FakeTransport(404)).use { session ->
            assertThrows<ImageHttpException> { runBlocking { session.fetchImage(image("https://example.test/p/1.webp")) } }
            assertNotEquals(SessionState.LIMITED, session.state)
        }
    }

    @Test
    fun `retry-after as a date is ignored`() {
        val transport = FakeTransport(429).apply { headers.add("retry-after" to "Wed, 21 Oct 2026 07:28:00 GMT") }
        open(transport).use { session ->
            val e = assertThrows<SourceLimitedException> { runBlocking { session.fetchImage(image("https://example.test/p/1.webp")) } }
            assertNull(e.retryAfterSeconds)
        }
    }
}
