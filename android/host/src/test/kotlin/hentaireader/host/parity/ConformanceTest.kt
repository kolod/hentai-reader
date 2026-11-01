package hentaireader.host.parity

import hentaireader.host.engine.LoadException
import java.io.File
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Assumptions.assumeTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Single-defect conformance samples behave on Android as in the runner and on Windows (§3). */
class ConformanceTest {
    private suspend fun open(sample: String) =
        File(Repo.conformance, sample).let { Repo.open(File(it, "$sample.js"), File(it, "fixtures"), enforcePoliteness = false) }

    @Test
    fun `forbidden capability is named`() = runBlocking {
        open("broken-forbidden-capability").use { session ->
            val res = session.call("search", listOf(Json.parseToJsonElement("""{"text":"","filters":{}}"""), null))
            assertFalse(res.ok)
            assertEquals("forbidden capability: fetch", res.error!!.message)
        }
    }

    @Test
    fun `incompatible contract version is refused`() {
        val e = assertThrows<LoadException> { runBlocking { open("broken-contract-version") } }
        assertTrue(e.message!!.contains("requires contract 2.0"), e.message)
    }

    @Test
    fun `login page outside the domains is refused`() {
        val e = assertThrows<LoadException> { runBlocking { open("broken-login-page") } }
        assertTrue(e.message!!.contains("manifest.loginPage.url: host elsewhere.test is outside manifest.domains"), e.message)
    }

    @Test
    fun `engine is the runner's QuickJS build`() {
        val runnerVariant = File(Repo.root, "node_modules/@jitl/quickjs-wasmfile-release-sync/README.md")
        assumeTrue(runnerVariant.exists(), "node_modules not installed; run npm ci to compare engine versions")
        val commit = Regex("bellard/quickjs/commit/([0-9a-f]{40})").find(runnerVariant.readText())!!.groupValues[1]
        val ours = File(Repo.root, "android/quickjs/QUICKJS_VERSION").readText()
        assertTrue(ours.contains(commit), "android/quickjs/QUICKJS_VERSION ($ours) differs from the runner's QuickJS $commit")
    }
}
