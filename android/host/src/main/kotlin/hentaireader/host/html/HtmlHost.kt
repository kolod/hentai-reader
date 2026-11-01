package hentaireader.host.html

import hentaireader.host.engine.Envelope
import hentaireader.host.engine.SourceErrorKind
import hentaireader.host.engine.toJson
import org.jsoup.Jsoup
import org.jsoup.nodes.DataNode
import org.jsoup.nodes.Document
import org.jsoup.nodes.Element
import org.jsoup.nodes.Node
import org.jsoup.nodes.TextNode
import org.jsoup.select.NodeTraversor
import org.jsoup.select.NodeVisitor
import org.jsoup.select.Selector

/**
 * html.select for the Android host, backed by jsoup, held to the contract's portable rules
 * (specs/003-windows-host/contracts/host-bridge.md and specs/007-android-app research R3):
 *  - the shared pre-check of non-standard pseudo-classes, plus jsoup's own extensions, which the
 *    shared regex does not catch and the other hosts reject;
 *  - descendants only (jsoup's select also matches the context element);
 *  - text with textContent semantics (jsoup's text() adds spaces at blocks and `<br>`);
 *  - markup without pretty printing.
 */
class HtmlHost {
    private val docs = ArrayDeque<Pair<String, Document>>()
    private val handles = HashMap<Int, Element>()
    private var nextId = 1

    fun select(markup: String, elementId: Int, selector: String): String {
        FORBIDDEN_PSEUDO.find(selector)?.let {
            return Envelope.error(SourceErrorKind.INVALID_INPUT, "html.select: non-standard selector ${it.value} is not supported")
        }
        JSOUP_ONLY.find(selector)?.let {
            return Envelope.error(SourceErrorKind.INVALID_INPUT, "html.select: non-standard selector ${it.value} is not supported")
        }
        return try {
            val context: Element = if (elementId < 0) {
                document(markup)
            } else {
                handles[elementId] ?: return Envelope.error(SourceErrorKind.INVALID_INPUT, "html.select: element is no longer valid")
            }
            val result = context.select(selector)
                .filter { it !== context && it !is Document }
                .map { node ->
                    val id = nextId++
                    handles[id] = node
                    mapOf("id" to id, "tag" to node.normalName())
                }
            Envelope.value(toJson(result))
        } catch (e: Selector.SelectorParseException) {
            Envelope.error(SourceErrorKind.INVALID_INPUT, "html.select: invalid selector '$selector': ${e.message}")
        } catch (e: IllegalArgumentException) {
            Envelope.error(SourceErrorKind.INVALID_INPUT, "html.select: invalid selector '$selector': ${e.message}")
        }
    }

    fun call(op: String, elementId: Int, arg: String): String {
        val node = handles[elementId] ?: return Envelope.error(SourceErrorKind.INVALID_INPUT, "html: element is no longer valid")
        return when (op) {
            "text" -> Envelope.value(collapseWhitespace(textContent(node)))
            "ownText" -> Envelope.value(collapseWhitespace(ownText(node)))
            "html" -> Envelope.value(node.html())
            "outerHtml" -> Envelope.value(node.outerHtml())
            // HTML attribute names are case-insensitive, as in getAttribute.
            "attr" -> Envelope.value(if (node.attributes().hasKeyIgnoreCase(arg)) node.attributes().getIgnoreCase(arg) else null)
            else -> Envelope.error(SourceErrorKind.INVALID_INPUT, "html: unknown operation $op")
        }
    }

    /** Drops element handles; called when no source call is in flight. */
    fun releaseHandles() = handles.clear()

    private fun document(markup: String): Document {
        val hit = docs.indexOfFirst { it.first === markup || it.first == markup }
        if (hit >= 0) {
            val entry = docs.removeAt(hit)
            docs.addFirst(entry)
            return entry.second
        }
        val doc = Jsoup.parse(markup)
        doc.outputSettings().prettyPrint(false)
        docs.addFirst(markup to doc)
        if (docs.size > DOC_CACHE_SIZE) docs.removeLast()
        return doc
    }

    companion object {
        private const val DOC_CACHE_SIZE = 8

        private val FORBIDDEN_PSEUDO = Regex(
            ":(contains|icontains|eq|gt|lt|first|last|even|odd|header|parent|selected|checkbox|radio|password|file|submit|reset|button|input|text|image|animated|hidden|visible)(?![\\w-])",
            RegexOption.IGNORE_CASE,
        )

        /** jsoup extensions the shared regex lets through (letters after the name) or does not list. */
        private val JSOUP_ONLY = Regex(
            ":(containsOwn|containsData|containsWholeText|containsWholeOwnText|matches|matchesOwn|matchText|matchesWholeText|matchesWholeOwnText)(?![\\w-])|\\[[^\\]]*~=\\s*/",
            RegexOption.IGNORE_CASE,
        )

        private val ASCII_WHITESPACE = Regex("[ \t\n\r\u000C]+")

        fun collapseWhitespace(text: String) = ASCII_WHITESPACE.replace(text, " ").trim(' ')

        /** DOM textContent: every text and data (script, style) node below, in document order. */
        private fun textContent(element: Element): String {
            val sb = StringBuilder()
            NodeTraversor.traverse(NodeVisitor { node: Node, _: Int ->
                when (node) {
                    is TextNode -> sb.append(node.wholeText)
                    is DataNode -> sb.append(node.wholeData)
                }
            }, element)
            return sb.toString()
        }

        private fun ownText(element: Element): String {
            val sb = StringBuilder()
            for (child in element.childNodes()) {
                when (child) {
                    is TextNode -> sb.append(child.wholeText)
                    is DataNode -> sb.append(child.wholeData)
                }
            }
            return sb.toString()
        }
    }
}
