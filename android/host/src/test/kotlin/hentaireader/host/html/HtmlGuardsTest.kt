package hentaireader.host.html

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import org.junit.jupiter.api.Test

/** jsoup held to the contract's portable rules (research R3, parity-suite-android.md §4). */
class HtmlGuardsTest {
    private val host = HtmlHost()

    private fun select(markup: String, selector: String, element: Int = -1): JsonObject =
        Json.parseToJsonElement(host.select(markup, element, selector)).jsonObject

    private fun ids(env: JsonObject) = (env["value"] as JsonArray).map { it.jsonObject["id"]!!.jsonPrimitive.int }

    private fun call(op: String, id: Int, arg: String = "") =
        (Json.parseToJsonElement(host.call(op, id, arg)).jsonObject["value"] as? JsonPrimitive)?.contentOrNull

    @ParameterizedTest(name = "Html.JsoupOnlySelectors {0}")
    @ValueSource(strings = [
        "p:containsOwn(x)", "p:containsData(x)", "p:containsWholeText(x)", "p:containsWholeOwnText(x)",
        "p:matches(x)", "p:matchesOwn(x)", "p:matchText", "p:matchesWholeText(x)", "p:matchesWholeOwnText(x)",
        "p:contains(x)", "p:lt(1)", "p:eq(0)", "[class~=/x/]",
    ])
    fun `jsoup-only and non-standard selectors are refused`(selector: String) {
        val env = select("<p>x</p>", selector)
        assertEquals("InvalidInput", env["error"]?.jsonObject?.get("kind")?.jsonPrimitive?.content, env.toString())
    }

    @Test
    fun `Html DescendantsOnly - an element never matches itself`() {
        val div = ids(select("<div class='a'><div class='a'><div class='a'></div></div></div>", "div.a")).first()
        assertEquals(2, ids(select("", "*", div)).size)
        assertEquals(2, ids(select("", "div.a", div)).size)
    }

    @Test
    fun `Html TextSemantics - textContent, not jsoup text`() {
        val p = ids(select("<p>a<br>b<span>c</span> d&nbsp;e <script>s()</script></p>", "p")).first()
        assertEquals("ab", call("text", ids(select("<i>a<br>b</i>", "i")).first()))
        assertTrue(call("text", p)!!.contains("d e"), "NBSP kept")
        assertEquals("own text", call("ownText", ids(select("<p>own <i>inner</i> text</p>", "p")).first()))
        assertEquals("s()", call("text", ids(select("<script>s()</script>", "script")).first()))
    }

    @Test
    fun `html is not pretty printed`() {
        val div = ids(select("<div>\n  <p>a</p><p>b</p></div>", "div")).first()
        assertEquals("\n  <p>a</p><p>b</p>", call("html", div))
    }

    @Test
    fun `attr is case-insensitive and null when absent`() {
        val a = ids(select("<a HREF='/x'>y</a>", "a")).first()
        assertEquals("/x", call("attr", a, "href"))
        assertEquals(null, call("attr", a, "title"))
    }
}
