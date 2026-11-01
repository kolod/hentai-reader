package hentaireader.host.archive

import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject

/**
 * `hentai-reader.json`, archive format 1 (contract/archive/README.md; port of the Windows
 * ArchiveMetadata). Unknown fields are kept when the file is read and written back, so a newer
 * app's additions survive an update.
 */
data class ArchiveMetadata(
    val format: Int = CURRENT_FORMAT,
    val app: String? = null,
    val source: Source,
    val title: String,
    val titleOriginal: String? = null,
    val language: String? = null,
    val type: String? = null,
    val uploader: String? = null,
    val uploadedAt: Instant? = null,
    val tags: List<Tag> = emptyList(),
    val savedAt: Instant,
    val pages: List<Page>,
    val progress: ProgressBlock? = null,
    val reader: ReaderBlock? = null,
    val extra: Map<String, JsonElement> = emptyMap(),
) {
    data class Source(val id: String, val name: String? = null, val galleryId: String, val webUrl: String? = null)
    data class Tag(val namespace: String?, val name: String)
    data class Page(val file: String, val width: Int, val height: Int)
    data class ProgressBlock(val page: Int, val offset: Double, val finished: Boolean, val updatedAt: Instant)
    data class ReaderBlock(val zoom: Double, val fitWidth: Boolean, val gaps: Boolean, val updatedAt: Instant)

    fun toJson(): JsonObject {
        val known = linkedMapOf<String, JsonElement>(
            "format" to JsonPrimitive(format),
            "app" to str(app),
            "source" to JsonObject(linkedMapOf("id" to JsonPrimitive(source.id), "name" to str(source.name),
                "galleryId" to JsonPrimitive(source.galleryId), "webUrl" to str(source.webUrl))),
            "title" to JsonPrimitive(title),
            "titleOriginal" to str(titleOriginal),
            "language" to str(language),
            "type" to str(type),
            "uploader" to str(uploader),
            "uploadedAt" to str(uploadedAt?.toString()),
            "tags" to JsonArray(tags.map { JsonObject(linkedMapOf("namespace" to str(it.namespace), "name" to JsonPrimitive(it.name))) }),
            "savedAt" to JsonPrimitive(savedAt.toString()),
            "pages" to JsonArray(pages.map { JsonObject(linkedMapOf("file" to JsonPrimitive(it.file), "width" to JsonPrimitive(it.width), "height" to JsonPrimitive(it.height))) }),
            "progress" to (progress?.let { JsonObject(linkedMapOf("page" to JsonPrimitive(it.page), "offset" to JsonPrimitive(it.offset),
                "finished" to JsonPrimitive(it.finished), "updatedAt" to JsonPrimitive(it.updatedAt.toString()))) } ?: JsonNull),
            "reader" to (reader?.let { JsonObject(linkedMapOf("zoom" to JsonPrimitive(it.zoom), "fitWidth" to JsonPrimitive(it.fitWidth),
                "gaps" to JsonPrimitive(it.gaps), "updatedAt" to JsonPrimitive(it.updatedAt.toString()))) } ?: JsonNull),
        )
        return JsonObject(known + extra.filterKeys { it !in known })
    }

    fun toUtf8(): ByteArray = PRETTY.encodeToString(JsonObject.serializer(), toJson()).toByteArray(Charsets.UTF_8)

    /** ComicInfo.xml (2.0 subset) for third-party readers; written once with the archive. */
    fun comicInfoXml(): ByteArray {
        val sb = StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n")
        sb.append("<ComicInfo xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n")
        fun element(name: String, value: String?) {
            if (!value.isNullOrEmpty()) sb.append("  <").append(name).append('>').append(escape(value)).append("</").append(name).append(">\n")
        }
        fun join(ns: String) = tags.filter { it.namespace == ns }.joinToString(", ") { it.name }
        element("Title", title)
        element("Series", title)
        element("Writer", join("artist"))
        element("Penciller", join("group"))
        element("Genre", type)
        element("Tags", tags.joinToString(", ") { if (it.namespace.isNullOrEmpty()) it.name else "${it.namespace}:${it.name}" })
        element("Web", source.webUrl)
        element("LanguageISO", language)
        element("PageCount", pages.size.toString())
        sb.append("  <Pages>\n")
        pages.forEachIndexed { i, p ->
            sb.append("    <Page Image=\"").append(i).append('"')
            if (i == 0) sb.append(" Type=\"FrontCover\"")
            sb.append(" ImageWidth=\"").append(p.width).append("\" ImageHeight=\"").append(p.height).append("\" />\n")
        }
        sb.append("  </Pages>\n</ComicInfo>\n")
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        const val ENTRY_NAME = "hentai-reader.json"
        const val COMIC_INFO_NAME = "ComicInfo.xml"
        const val CURRENT_FORMAT = 1
        private val PRETTY = Json { prettyPrint = true }
        private val KNOWN = setOf("format", "app", "source", "title", "titleOriginal", "language", "type", "uploader",
            "uploadedAt", "tags", "savedAt", "pages", "progress", "reader")

        private fun str(s: String?): JsonElement = if (s == null) JsonNull else JsonPrimitive(s)
        private fun escape(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")

        /** Format number of a metadata file without full parsing (null when unreadable). */
        fun formatOf(utf8: ByteArray): Int? = runCatching {
            (Json.parseToJsonElement(String(utf8, Charsets.UTF_8)).jsonObject["format"] as? JsonPrimitive)?.intOrNull
        }.getOrNull()

        /** Parses format 1; returns null for another format (a newer app wrote it) or invalid JSON. */
        fun parse(utf8: ByteArray): ArchiveMetadata? = runCatching {
            val o = Json.parseToJsonElement(String(utf8, Charsets.UTF_8).removePrefix("﻿")).jsonObject
            if ((o["format"] as? JsonPrimitive)?.intOrNull != CURRENT_FORMAT) return null
            val s = o["source"]!!.jsonObject
            ArchiveMetadata(
                format = CURRENT_FORMAT,
                app = o.s("app"),
                source = Source(s.s("id")!!, s.s("name"), s.s("galleryId")!!, s.s("webUrl")),
                title = o.s("title") ?: "",
                titleOriginal = o.s("titleOriginal"),
                language = o.s("language"),
                type = o.s("type"),
                uploader = o.s("uploader"),
                uploadedAt = o.s("uploadedAt")?.let { runCatching { Instant.parse(it) }.getOrNull() },
                tags = (o["tags"] as? JsonArray).orEmpty().map { val t = it.jsonObject; Tag(t.s("namespace"), t.s("name") ?: "") },
                savedAt = o.s("savedAt")?.let { runCatching { Instant.parse(it) }.getOrNull() } ?: Instant.EPOCH,
                pages = (o["pages"] as JsonArray).map { val p = it.jsonObject; Page(p.s("file")!!, p.i("width") ?: 0, p.i("height") ?: 0) },
                progress = (o["progress"] as? JsonObject)?.let { p ->
                    ProgressBlock(p.i("page") ?: 0, (p["offset"] as? JsonPrimitive)?.doubleOrNull ?: 0.0,
                        (p["finished"] as? JsonPrimitive)?.booleanOrNull ?: false, p.time("updatedAt"))
                },
                reader = (o["reader"] as? JsonObject)?.let { r ->
                    ReaderBlock((r["zoom"] as? JsonPrimitive)?.doubleOrNull ?: 1.0, (r["fitWidth"] as? JsonPrimitive)?.booleanOrNull ?: true,
                        (r["gaps"] as? JsonPrimitive)?.booleanOrNull ?: true, r.time("updatedAt"))
                },
                extra = o.filterKeys { it !in KNOWN },
            )
        }.getOrNull()

        private fun JsonObject.s(k: String) = (this[k] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
        private fun JsonObject.i(k: String) = (this[k] as? JsonPrimitive)?.intOrNull
        private fun JsonObject.time(k: String) = s(k)?.let { runCatching { Instant.parse(it) }.getOrNull() } ?: Instant.EPOCH
    }
}
