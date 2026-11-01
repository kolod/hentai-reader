package hentaireader.host.parity

import hentaireader.host.http.CookieJar
import hentaireader.host.http.RequestLogEntry
import java.io.File
import java.net.URI
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.TestFactory

/**
 * The portable host contract suite (every directory of contract/tests) run against the Android host, exactly as
 * `source-runner contract-test` and the Windows suite run it (contracts/parity-suite-android.md §1).
 */
class ContractSuiteTest {
    @TestFactory
    fun contract(): List<DynamicTest> =
        (Repo.contractTests.listFiles() ?: emptyArray())
            .filter { File(it, "test.js").exists() }
            .sortedBy { it.name }
            .map { dir -> DynamicTest.dynamicTest("Contract.${dir.name}") { runBlocking { run(dir) } } }

    private suspend fun run(dir: File) {
        val expectFile = File(dir, "expect.json")
        val expect = if (expectFile.exists()) Json.parseToJsonElement(expectFile.readText()).jsonObject else JsonObject(emptyMap())
        val problems = mutableListOf<String>()

        suspend fun execute(script: String): List<RequestLogEntry> {
            val cookies = CookieJar()
            ((expect["cookies"] as? JsonObject)?.get("seed") as? JsonObject)?.forEach { (domain, value) ->
                cookies.seed(domain, value.jsonPrimitive.content)
            }
            // Storage is per script: the isolation test runs two different sources.
            Repo.open(File(dir, script), File(dir, "fixtures"), enforcePoliteness = true, cookies = cookies).use { session ->
                val settings = (expect["settings"] as? JsonObject)?.mapValues { it.value.jsonPrimitive.content }
                val res = session.call("run", emptyList(), settings)
                if (!res.ok) {
                    val e = res.error!!
                    problems.add("$script: ${if (e.type == "SourceError") "SourceError(${e.kind})" else e.name}: ${e.message}")
                }
                return synchronized(session.http.log) { session.http.log.toList() }
            }
        }

        (expect["other"] as? JsonPrimitive)?.let { execute(it.content) }
        val log = execute("test.js")
        problems.addAll(assertLog(log, expect))
        assertTrue(problems.isEmpty(), problems.joinToString("\n"))
    }

    private fun assertLog(log: List<RequestLogEntry>, expect: JsonObject): List<String> {
        val out = mutableListOf<String>()
        val sent = log.filter { it.refused == null }
        (expect["minGapMs"] as? JsonPrimitive)?.let { gapNode ->
            val minGap = gapNode.int
            val starts = sent.map { it.startedAt }.sorted()
            if (starts.size < 2) out.add("minGapMs: fewer than two requests were sent")
            for (i in 1 until starts.size) {
                val gap = starts[i] - starts[i - 1]
                if (gap < minGap - GAP_TOLERANCE_MS) out.add("request start gap $gap ms < minGapMs $minGap")
            }
        }
        (expect["maxInFlight"] as? JsonPrimitive)?.let { maxNode ->
            var max = 0
            for (r in sent) {
                val overlapping = sent.count { o -> o !== r && o.startedAt <= r.startedAt && (o.endedAt ?: Long.MAX_VALUE) > r.startedAt }
                max = maxOf(max, overlapping + 1)
            }
            val expected = maxNode.int
            if (max > expected) out.add("$max requests in flight > maxInFlight $expected")
            if (max < expected) out.add("only $max requests in flight; the test does not exercise maxInFlight $expected")
        }
        for (url in (expect["refusedUrls"] as? JsonArray)?.map { it.jsonPrimitive.content }.orEmpty()) {
            if (log.none { it.refused != null && it.url == url }) out.add("expected the host to refuse $url")
        }
        fun hostOf(r: RequestLogEntry) = URI(r.url).host
        val cookies = expect["cookies"] as? JsonObject
        for (host in (cookies?.get("sentTo") as? JsonArray)?.map { it.jsonPrimitive.content }.orEmpty()) {
            if (sent.none { hostOf(it) == host && it.headers.containsKey("cookie") }) out.add("expected cookies to be sent to $host")
        }
        for (host in (cookies?.get("notSentTo") as? JsonArray)?.map { it.jsonPrimitive.content }.orEmpty()) {
            if (sent.any { hostOf(it) == host && it.headers.containsKey("cookie") }) out.add("cookies must not be sent to $host")
        }
        return out
    }

    private companion object {
        const val GAP_TOLERANCE_MS = 2
    }
}
