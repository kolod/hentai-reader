package hentaireader.host.engine

import hentaireader.host.fixtures.OfflineTransport
import hentaireader.host.parity.Repo
import hentaireader.host.sources.SessionOptions
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.SourceStorage
import java.nio.file.Files
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/** Engine limits and string transfer (contracts/parity-suite-android.md §4). */
class EngineTest {
    private fun source(body: String) = """
        export const manifest = {
          id: "t", name: "t", version: "1.0.0", contract: "1.0", domains: ["a.test"],
          politeness: { minIntervalMs: 0, maxConcurrency: 1 }, login: "none", filters: [],
          capabilities: { suggestTags: false },
        };
        $body
    """.trimIndent()

    private suspend fun open(body: String, limits: EngineLimits = EngineLimits.DEFAULT): SourceSession {
        val empty = Files.createTempDirectory("fixtures").toFile()
        val options = SessionOptions(OfflineTransport(empty), false, SourceStorage(), Repo.scripts, limits = limits)
        return SourceSession.open(source(body), "t.js", options)
    }

    @Test
    fun `Engine Interrupt - an endless loop ends at the deadline and the session recovers`() = runBlocking {
        open(
            """
            export async function search() { for (;;) {} }
            export async function getGallery(id) { return { id }; }
            """,
            EngineLimits(callTimeoutMs = 500, memoryBytes = 128L * 1024 * 1024),
        ).use { session ->
            val started = System.nanoTime()
            val res = session.call("search", listOf(null, null))
            assertFalse(res.ok)
            assertEquals("ScriptError", res.error!!.type)
            assertEquals("time", res.error.details?.get("limit")?.jsonPrimitive?.content)
            assertTrue((System.nanoTime() - started) / 1_000_000 < 5_000, "interrupted promptly")
            val next = session.call("getGallery", listOf(JsonPrimitive("1")))
            assertTrue(next.ok, "the next call runs on a fresh engine: ${next.error}")
        }
    }

    @Test
    fun `Engine Memory - allocation beyond the limit is a memory limit error`() = runBlocking {
        open(
            """
            export async function search() { const a = []; for (;;) a.push(new Array(1e5).fill(1)); }
            export async function getGallery(id) { return { id }; }
            """,
            EngineLimits(callTimeoutMs = 30_000, memoryBytes = 32L * 1024 * 1024),
        ).use { session ->
            val res = session.call("search", listOf(null, null))
            assertFalse(res.ok)
            assertEquals("memory", res.error!!.details?.get("limit")?.jsonPrimitive?.content, res.error.toString())
            assertTrue(session.call("getGallery", listOf(JsonPrimitive("1"))).ok, "engine recreated for the next call")
        }
    }

    @Test
    fun `strings outside the BMP and lone surrogates survive the bridge both ways`() = runBlocking {
        open(
            """
            export async function getGallery(id) {
              return { id, title: id + "\u{1F600}", lone: "\uD800", len: id.length };
            }
            """,
        ).use { session ->
            val res = session.call("getGallery", listOf(JsonPrimitive("漫画😀")))
            assertTrue(res.ok, res.error.toString())
            val value = res.value as kotlinx.serialization.json.JsonObject
            assertEquals("漫画😀😀", value["title"]!!.jsonPrimitive.content)
            assertEquals("\uD800", value["lone"]!!.jsonPrimitive.content)
            assertEquals(4, value["len"]!!.jsonPrimitive.content.toInt())
        }
    }

    @Test
    fun `a syntax error refuses the source at load`() {
        val e = runCatching { runBlocking { open("export async function search( {") } }.exceptionOrNull()
        assertTrue(e is LoadException && e.message!!.startsWith("t.js: SyntaxError"), e.toString())
    }

}
