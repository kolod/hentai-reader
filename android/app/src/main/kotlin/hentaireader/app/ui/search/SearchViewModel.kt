package hentaireader.app.ui.search

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import hentaireader.app.AppServices
import hentaireader.app.ui.common.ErrorInfo
import hentaireader.host.engine.LoadException
import hentaireader.host.sources.SourceSession
import hentaireader.host.DefaultQuery
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

enum class SearchStatus { IDLE, LOADING, END, ERROR }

/** Current value of one declared filter (manifest FilterDefinition). */
class FilterState(val id: String, val kind: String, val definition: JsonObject) {
    var single by mutableStateOf<String?>(null)
    var number by mutableStateOf<Double?>(null)
    var min by mutableStateOf<Double?>(null)
    var max by mutableStateOf<Double?>(null)
    val multi = mutableStateListOf<String>()

    val values: List<String> get() = (definition["values"] as? JsonArray)?.map { it.jsonPrimitive.content }.orEmpty()
    val default: String? get() = (definition["default"] as? JsonPrimitive)?.contentOrNull
    val lower: Double get() = (definition["min"] as? JsonPrimitive)?.doubleOrNull ?: 0.0
    val upper: Double get() = (definition["max"] as? JsonPrimitive)?.doubleOrNull ?: 0.0

    /** Contract SearchQuery.filters entry, or null when unset. */
    fun toJson(): JsonElement? = when (kind) {
        "single" -> single?.let { JsonPrimitive(it) }
        "multi" -> if (multi.isEmpty()) null else JsonArray(multi.sorted().map { JsonPrimitive(it) })
        "number" -> number?.let { JsonPrimitive(it) }
        "range" -> if (min == null && max == null) null else JsonObject(buildMap {
            min?.let { put("min", JsonPrimitive(it)) }
            max?.let { put("max", JsonPrimitive(it)) }
        })
        else -> null
    }

    fun reset() {
        single = if (kind == "single") default else null
        number = null
        min = null
        max = null
        multi.clear()
    }

    /** Sets the value from a saved `SearchQuery.filters` entry; a single saved value counts as a one-item list. */
    fun apply(v: JsonElement) {
        reset()
        when (kind) {
            "single" -> single = (v as? JsonPrimitive)?.contentOrNull
            "multi" -> multi.addAll((v as? JsonArray ?: JsonArray(listOf(v))).mapNotNull { (it as? JsonPrimitive)?.contentOrNull })
            "number" -> number = (v as? JsonPrimitive)?.doubleOrNull
            "range" -> (v as? JsonObject)?.let { o ->
                min = (o["min"] as? JsonPrimitive)?.doubleOrNull
                max = (o["max"] as? JsonPrimitive)?.doubleOrNull
            }
        }
    }
}

/**
 * Search state of one source (port of the Windows SearchViewModel): query, declared filters,
 * endless results via the source cursor, cancellation of superseded searches, error kinds, and the
 * limited state after RateLimited that only an explicit retry clears (003 FR-012, FR-013, FR-015).
 * Query, filters and the number of loaded pages survive the system stopping the app
 * (007 FR-025); the results are then loaded again.
 */
class SearchViewModel(
    private val services: AppServices,
    val sourceId: String,
    initialText: String?,
    private val saved: SavedStateHandle,
) : ViewModel() {
    var session by mutableStateOf<SourceSession?>(null)
        private set
    var loadError by mutableStateOf<String?>(null)
        private set
    var query by mutableStateOf(saved.get<String>(KEY_TEXT) ?: initialText ?: "")
    val filters = mutableStateListOf<FilterState>()
    val items = mutableStateListOf<JsonObject>()
    var status by mutableStateOf(SearchStatus.IDLE)
        private set
    var error by mutableStateOf<ErrorInfo?>(null)
        private set
    var total by mutableStateOf<Long?>(null)
        private set

    val sourceName: String get() = services.sources.value?.firstOrNull { it.id == sourceId }?.name ?: sourceId
    val hasMore: Boolean get() = status != SearchStatus.END && status != SearchStatus.ERROR && (!started || cursor != null)

    private var cursor: String? = null
    private var started = false
    private var pages = 0
    private var generation = 0
    private var job: Job? = null

    init {
        viewModelScope.launch {
            val s = try {
                services.session(sourceId)
            } catch (e: LoadException) {
                loadError = e.message
                return@launch
            }
            for (f in (s.manifest["filters"] as? JsonArray).orEmpty()) {
                val def = f.jsonObject
                filters.add(FilterState(def["id"]!!.jsonPrimitive.content, def["kind"]!!.jsonPrimitive.content, def).apply { reset() })
            }
            val restored = saved.get<String>(KEY_FILTERS)
            if (restored != null) applyFilters(Json.parseToJsonElement(restored).jsonObject)
            else if (initialText == null && saved.get<String>(KEY_TEXT) == null) {
                // Opening a source runs its default search (006 FR-017).
                services.prefs.value.defaultQueries[sourceId]?.let { query = it.text; applyFilters(it.filters) }
            }
            session = s
            search(restorePages = saved.get<Int>(KEY_PAGES) ?: 1)
        }
    }

    /** The contract `SearchQuery.filters` for the current filter values. */
    fun currentFilters(): JsonObject = JsonObject(filters.mapNotNull { f -> f.toJson()?.let { f.id to it } }.toMap())

    /** Sets the filters from a saved `SearchQuery.filters`; unknown ids are ignored. */
    fun applyFilters(json: JsonObject) {
        for (f in filters) {
            val v = json[f.id]
            if (v == null) f.reset() else f.apply(v)
        }
    }

    fun resetFilters() = filters.forEach { it.reset() }

    /** Whether the query and filters equal the saved default search (the star is filled). */
    fun isDefault(defaults: Map<String, DefaultQuery>): Boolean =
        defaults[sourceId]?.let { it.text == query.trim() && it.filters == currentFilters() } == true

    /** Saves the current query and filters as the source's default search (006 FR-016). */
    fun saveDefault() = viewModelScope.launch {
        val q = DefaultQuery(query.trim(), currentFilters())
        services.updatePrefs { it.copy(defaultQueries = it.defaultQueries + (sourceId to q)) }
    }

    fun clearDefault() = viewModelScope.launch { services.updatePrefs { it.copy(defaultQueries = it.defaultQueries - sourceId) } }

    /** Starts a new search; the one still running is abandoned. */
    fun search(restorePages: Int = 1) {
        job?.cancel()
        generation++
        cursor = null
        started = false
        pages = 0
        items.clear()
        total = null
        error = null
        status = SearchStatus.IDLE
        saved[KEY_TEXT] = query
        saved[KEY_FILTERS] = currentFilters().toString()
        loadMore(restorePages)
    }

    /** Clears a rate limit (the user's explicit retry) and continues or restarts. */
    fun retry() {
        session?.clearLimit()
        if (started && items.isNotEmpty()) {
            error = null
            status = SearchStatus.IDLE
            loadMore()
        } else {
            search()
        }
    }

    /** Loads the next page; does nothing while one is loading or at the end. */
    fun loadMore(untilPages: Int = 1) {
        val s = session ?: return
        if (status == SearchStatus.LOADING || !hasMore) return
        val gen = generation
        status = SearchStatus.LOADING
        job = viewModelScope.launch {
            do {
                val q = JsonObject(mapOf("text" to JsonPrimitive(query.trim()), "filters" to currentFilters()))
                val result = services.call(s, sourceId, "search", listOf(q, cursor?.let { JsonPrimitive(it) } ?: JsonNull))
                if (gen != generation) return@launch
                started = true
                if (!result.ok) {
                    error = ErrorInfo.of(result.error!!, sourceName)
                    status = SearchStatus.ERROR
                    return@launch
                }
                val page = result.value!!.jsonObject
                page["items"]?.jsonArray?.forEach { items.add(it.jsonObject) }
                cursor = (page["nextCursor"] as? JsonPrimitive)?.contentOrNull
                (page["total"] as? JsonPrimitive)?.doubleOrNull?.let { total = it.toLong() }
                pages++
                saved[KEY_PAGES] = pages
            } while (pages < untilPages && cursor != null)
            status = if (cursor == null) SearchStatus.END else SearchStatus.IDLE
        }
    }

    private companion object {
        const val KEY_TEXT = "text"
        const val KEY_FILTERS = "filters"
        const val KEY_PAGES = "pages"
    }
}
