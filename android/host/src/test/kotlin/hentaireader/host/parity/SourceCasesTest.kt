package hentaireader.host.parity

import hentaireader.host.storage.SourceStorage
import java.io.File
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.TestFactory

/**
 * Every recorded case of every source and conformance sample, replayed on the Android host and
 * compared with the runner's snapshot (contracts/parity-suite-android.md §2; port of the Windows
 * CaseRunner and SourceCases). Snapshots are read-only here.
 */
class SourceCasesTest {
    @TestFactory
    fun cases(): List<DynamicTest> {
        val tests = mutableListOf<DynamicTest>()
        for (root in listOf(Repo.sources, Repo.conformance)) {
            for (dir in (root.listFiles() ?: emptyArray()).sortedBy { it.name }) {
                val testsDir = File(dir, "tests")
                if (!File(dir, "${dir.name}.js").exists() || !testsDir.isDirectory) continue
                // Broken samples are covered by ConformanceTest (their cases expect violations).
                if (dir.name.startsWith("broken-")) continue
                for (file in (testsDir.listFiles { f -> f.name.endsWith(".case.json") } ?: emptyArray()).sortedBy { it.name }) {
                    val name = file.name.removeSuffix(".case.json")
                    tests += DynamicTest.dynamicTest("Case.${dir.name}.$name") { runBlocking { run(dir, name) } }
                }
            }
        }
        return tests
    }

    private suspend fun run(sourceDir: File, caseName: String) {
        val testsDir = File(sourceDir, "tests")
        val case = Json.parseToJsonElement(File(testsDir, "$caseName.case.json").readText()).jsonObject
        val call = case["call"]!!.jsonObject
        val args = substitute(call["args"]!!, testsDir)!!.jsonArray.toList()

        var fixtures = File(sourceDir, "fixtures")
        (case["fixtures"] as? JsonPrimitive)?.let { fixtures = File(fixtures, it.content) }
        val storage = SourceStorage()
        (case["storage"] as? JsonObject)?.let { seed -> storage.seed(seed.mapValues { it.value.jsonPrimitive.content }) }

        val result = Repo.open(File(sourceDir, "${sourceDir.name}.js"), fixtures, enforcePoliteness = false, storage = storage, frozen = true).use { session ->
            val settings = (case["settings"] as? JsonObject)?.mapValues { it.value.jsonPrimitive.content }
            session.call(call["fn"]!!.jsonPrimitive.content, args, settings)
        }

        val expectedKind = ((case["expectError"] as? JsonObject)?.get("kind") as? JsonPrimitive)?.contentOrNull
        if (expectedKind != null) {
            assertFalse(result.ok, "expected SourceError($expectedKind), got a result")
            val e = result.error!!
            assertTrue(e.type == "SourceError" && e.kind == expectedKind,
                "expected SourceError($expectedKind), got ${e.type}(${e.kind ?: e.name}): ${e.message}")
            return
        }
        assertTrue(result.ok, "unexpected ${result.error?.type}(${result.error?.kind ?: result.error?.name}): ${result.error?.message}")
        val expectedFile = File(testsDir, "$caseName.expected.json")
        assertTrue(expectedFile.exists(), "missing snapshot ${expectedFile.name}")
        val expected = Json.parseToJsonElement(expectedFile.readText())
        assertNotNull(expected)
        val diff = jsonDiff(expected, result.value).take(30)
        assertTrue(diff.isEmpty(), "result differs from the runner's snapshot:\n" + diff.joinToString("\n"))
    }

    /** Replaces "@case" / "@case.path" strings at any depth with snapshot values. */
    private fun substitute(node: JsonElement, testsDir: File): JsonElement? = when {
        node is JsonPrimitive && node.isString && node.content.startsWith("@") -> {
            val parts = node.content.substring(1).split('.')
            var snapshot: JsonElement? = Json.parseToJsonElement(File(testsDir, "${parts[0]}.expected.json").readText())
            for (key in parts.drop(1)) {
                snapshot = when (val s = snapshot) {
                    is JsonObject -> s[key]
                    is JsonArray -> key.toIntOrNull()?.let { s.getOrNull(it) }
                    else -> null
                }
            }
            snapshot
        }
        node is JsonArray -> JsonArray(node.map { substitute(it, testsDir) ?: kotlinx.serialization.json.JsonNull })
        node is JsonObject -> JsonObject(node.mapValues { substitute(it.value, testsDir) ?: kotlinx.serialization.json.JsonNull })
        else -> node
    }
}
