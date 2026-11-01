package hentaireader.host.parity

import hentaireader.host.engine.ContractScripts
import hentaireader.host.engine.EngineLimits
import hentaireader.host.fixtures.OfflineTransport
import hentaireader.host.http.CookieJar
import hentaireader.host.sources.SessionOptions
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.SourceStorage
import java.io.File
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.doubleOrNull

/** Repository locations used by the parity suite: contract tests, sources, conformance samples. */
object Repo {
    val root: File = File(System.getProperty("repoRoot") ?: findRoot()).absoluteFile

    val contractTests get() = File(root, "contract/tests")
    val sources get() = File(root, "sources")
    val conformance get() = File(root, "contract/conformance")

    /** The contract scripts as shipped: read from their canonical files, never copied. */
    val scripts: ContractScripts by lazy {
        ContractScripts(File(root, "contract/prelude.js").readText(), File(root, "contract/host-glue.js").readText())
    }

    suspend fun open(
        script: File,
        fixtures: File,
        enforcePoliteness: Boolean,
        storage: SourceStorage = SourceStorage(),
        cookies: CookieJar? = null,
        frozen: Boolean = false,
        limits: EngineLimits = EngineLimits.DEFAULT,
    ): SourceSession {
        val transport = OfflineTransport(fixtures)
        val options = SessionOptions(
            transport, enforcePoliteness, storage, scripts, cookies,
            frozenNow = if (frozen) transport.recordedAt else null, limits = limits,
        )
        return SourceSession.open(script.readText(), script.name, options)
    }

    private fun findRoot(): String {
        var dir: File? = File("").absoluteFile
        while (dir != null) {
            if (File(dir, "contract/host-api.d.ts").exists()) return dir.path
            dir = dir.parentFile
        }
        error("repository root not found")
    }
}

/** JSON equality with numbers compared by value, as JSON.parse in the runner compares them. */
fun jsonEquals(a: JsonElement?, b: JsonElement?): Boolean = when {
    (a == null || a == JsonNull) && (b == null || b == JsonNull) -> true
    a is JsonObject && b is JsonObject -> a.keys == b.keys && a.keys.all { jsonEquals(a[it], b[it]) }
    a is JsonArray && b is JsonArray -> a.size == b.size && a.indices.all { jsonEquals(a[it], b[it]) }
    a is JsonPrimitive && b is JsonPrimitive && a !is JsonNull && b !is JsonNull ->
        if (a.isString || b.isString) a.isString == b.isString && a.content == b.content
        else a.doubleOrNull?.let { it == b.doubleOrNull } ?: (a.content == b.content)
    else -> false
}

/** Paths where two JSON values differ (for readable failures). */
fun jsonDiff(expected: JsonElement?, actual: JsonElement?, path: String = ""): List<String> {
    if (jsonEquals(expected, actual)) return emptyList()
    if (expected is JsonObject && actual is JsonObject) {
        return (expected.keys + actual.keys).sorted().flatMap { key ->
            val p = if (path.isEmpty()) key else "$path.$key"
            when {
                key !in actual -> listOf("- $p")
                key !in expected -> listOf("+ $p: ${show(actual[key])}")
                else -> jsonDiff(expected[key], actual[key], p)
            }
        }
    }
    if (expected is JsonArray && actual is JsonArray) {
        return (0 until maxOf(expected.size, actual.size)).flatMap { i ->
            val p = "$path[$i]"
            when {
                i >= actual.size -> listOf("- $p")
                i >= expected.size -> listOf("+ $p: ${show(actual[i])}")
                else -> jsonDiff(expected[i], actual[i], p)
            }
        }
    }
    return listOf("~ ${path.ifEmpty { "(root)" }}: ${show(expected)} → ${show(actual)}")
}

private fun show(n: JsonElement?): String {
    val s = n?.toString() ?: "null"
    return if (s.length > 80) s.take(77) + "…" else s
}
