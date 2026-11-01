package hentaireader.host

import hentaireader.host.storage.SourceStorage
import java.io.File
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject

/** Default search of a source: query text and filter values (006 FR-022). */
@Serializable
data class DefaultQuery(
    @SerialName("Text") val text: String,
    @SerialName("Filters") val filters: JsonObject = JsonObject(emptyMap()),
)

/**
 * App preferences, stored as `preferences.json` with the Windows field names and meanings
 * (007 FR-002, data-model). Android defaults differ where resources are limited (preload 3,
 * cache 512 MB); the three Android-only fields have no Windows counterpart.
 */
@Serializable
data class Preferences(
    /** "" = follow the system language; otherwise one of [LANGUAGES]. */
    @SerialName("Language") val language: String = "",
    @SerialName("AdultConfirmed") val adultConfirmed: Boolean = false,
    @SerialName("LastSourceId") val lastSourceId: String? = null,
    /** Pages kept loaded ahead of the view (3–5). */
    @SerialName("PreloadPages") val preloadPages: Int = 3,
    /** Maximum reading width in dp when the screen is wider (600–2000). */
    @SerialName("ReadingWidth") val readingWidth: Int = 1000,
    @SerialName("FitWidth") val fitWidth: Boolean = true,
    /** Gap between pages in dp (0–48, 0 = never). */
    @SerialName("PageGap") val pageGap: Int = 8,
    /** Page cache limit in MB (128–10240). */
    @SerialName("CacheLimitMb") val cacheLimitMb: Int = 512,
    @SerialName("NetworkCovers") val networkCovers: Boolean = false,
    /** last-source, source:<id>, history or library. */
    @SerialName("StartPage") val startPage: String = START_LAST_SOURCE,
    /** null = internal storage; `card:<volume uuid>` = a removable card. */
    @SerialName("CacheFolder") val cacheFolder: String? = null,
    /** The previous cache location while a move runs (continues after a restart). */
    @SerialName("CacheMoveFrom") val cacheMoveFrom: String? = null,
    @SerialName("DefaultQueries") val defaultQueries: Map<String, DefaultQuery> = emptyMap(),
    /** Per-source setting values (sourceId → settingId → value). */
    @SerialName("SourceSettings") val sourceSettings: Map<String, Map<String, String>> = emptyMap(),
    @SerialName("WifiOnlyDownloads") val wifiOnlyDownloads: Boolean = false,
    @SerialName("KeepScreenOn") val keepScreenOn: Boolean = true,
    @SerialName("NotificationAsked") val notificationAsked: Boolean = false,
) {
    /** Values within their documented ranges (a hand-edited or newer file cannot break the app). */
    fun sanitised() = copy(
        language = language.takeIf { it.isEmpty() || it in LANGUAGES } ?: "",
        preloadPages = preloadPages.coerceIn(3, 5),
        readingWidth = readingWidth.coerceIn(600, 2000),
        pageGap = pageGap.coerceIn(0, 48),
        cacheLimitMb = cacheLimitMb.coerceIn(128, 10240),
    )

    suspend fun save(file: File) = SourceStorage.writeAtomic(file, JSON.encodeToString(serializer(), this))

    companion object {
        val LANGUAGES = listOf("en-US", "de-DE", "fr-FR", "es-ES", "ja-JP", "uk-UA")
        const val START_LAST_SOURCE = "last-source"
        const val START_HISTORY = "history"
        const val START_LIBRARY = "library"
        const val START_SOURCE_PREFIX = "source:"

        private val JSON = Json { ignoreUnknownKeys = true; encodeDefaults = true; prettyPrint = true }

        /** Reads the file; a missing or unreadable file gives the defaults. */
        fun load(file: File): Preferences =
            runCatching { JSON.decodeFromString(serializer(), file.readText()) }.getOrNull()?.sanitised() ?: Preferences()
    }
}
