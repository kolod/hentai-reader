package hentaireader.host.sources

import hentaireader.host.fixtures.OfflineTransport
import hentaireader.host.parity.Repo
import hentaireader.host.storage.SourceStorage
import java.nio.file.Files
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/** Idle engines are closed and reopened transparently (007 T077). */
class SessionPoolTest {
    private val script = """
        export const manifest = {
          id: "t", name: "t", version: "1.0.0", contract: "1.0", domains: ["a.test"],
          politeness: { minIntervalMs: 0, maxConcurrency: 1 }, login: "none", filters: [],
          capabilities: { suggestTags: false },
        };
        export async function search() { return { items: [], nextCursor: null }; }
        export async function getGallery(id) {
          const n = Number(await storage.get("n")) + 1;   // Number(null) = 0
          await storage.set("n", String(n));
          return { id, n };
        }
    """.trimIndent()

    private suspend fun open(): SourceSession {
        val empty = Files.createTempDirectory("fixtures").toFile()
        return SourceSession.open(script, "t.js", SessionOptions(OfflineTransport(empty), false, SourceStorage(), Repo.scripts))
    }

    @Test
    fun `an idle engine is closed and the next call reopens it with the same storage`() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        open().use { session ->
            SessionPool(scope, idleMillis = 200).add(session)
            val first = session.call("getGallery", listOf(JsonPrimitive("1")))
            assertTrue(first.ok, "${first.error}")
            assertTrue(session.engineRunning)
            delay(800)
            assertFalse(session.engineRunning, "closed after the idle time")
            val second = session.call("getGallery", listOf(JsonPrimitive("1")))
            assertTrue(second.ok, "${second.error}")
            assertEquals(2, second.value!!.jsonObject["n"]!!.jsonPrimitive.content.toInt(), "storage kept across engines")
            assertEquals("t", (session.manifest["id"] as JsonPrimitive).content)
        }
        scope.cancel()
    }

    @Test
    fun `closeIdle closes engines at once and the next call reopens`() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        open().use { session ->
            val pool = SessionPool(scope, idleMillis = 60_000)
            pool.add(session)
            assertTrue(session.engineRunning)
            pool.closeIdle()
            assertFalse(session.engineRunning)
            assertTrue(session.call("search", listOf(null, null)).ok)
            assertTrue(session.engineRunning)
        }
        scope.cancel()
    }
}
