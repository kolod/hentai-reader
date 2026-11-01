package hentaireader.app.ui.reader

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import hentaireader.app.AppServices
import hentaireader.app.ui.common.ErrorInfo
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.HistoryRepository
import hentaireader.host.data.Progress
import hentaireader.host.data.ProgressRepository
import hentaireader.host.data.ReaderSettings
import hentaireader.host.data.ReaderSettingsRepository
import hentaireader.host.engine.LoadException
import hentaireader.host.reading.OnlinePageProvider
import hentaireader.host.reading.StripGeometry
import java.time.Instant
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * The reader of one gallery (005 US1–US5, 007 US3): the strip's position and zoom, the session
 * that loads pages, progress and per-gallery reader settings. Units are pixels. The position is
 * the page at the top of the view and the fraction of it above the view; a size correction above
 * the view shifts the scroll by the same amount, so the content in view never moves
 * (Constitution IV, 005 FR-006).
 */
class ReaderViewModel(
    private val services: AppServices,
    val sourceId: String,
    val galleryId: String,
    val title: String,
    /** Page to open at; -1 = continue at the saved position. */
    private val startPage: Int,
    private val saved: SavedStateHandle,
) : ViewModel() {
    var session by mutableStateOf<ReaderSession?>(null)
        private set
    var error by mutableStateOf<ErrorInfo?>(null)
        private set

    var scrollY by mutableDoubleStateOf(0.0)
        private set
    var offsetX by mutableDoubleStateOf(0.0)
        private set
    var zoom by mutableDoubleStateOf(1.0)
        private set
    var fitWidth by mutableStateOf(services.prefs.value.fitWidth)
        private set
    var gaps by mutableStateOf(true)
        private set

    var viewportWidth = 0.0
        private set
    var viewportHeight = 0.0
        private set
    private var density = 1f
    private var pendingPosition: Pair<Int, Double>? = null
    private var saveJob: Job? = null

    private val key = GalleryKey(sourceId, galleryId)

    init {
        viewModelScope.launch { open() }
    }

    private suspend fun open() {
        val s = try {
            services.session(sourceId)
        } catch (e: LoadException) {
            error = ErrorInfo("ScriptError", null, e.message ?: "")
            return
        }
        val gallery = services.galleries.get(key.toString()) ?: run {
            val result = services.call(s, sourceId, "getGallery", listOf(JsonPrimitive(galleryId)))
            if (!result.ok) {
                error = ErrorInfo.of(result.error!!, sourceId)
                return
            }
            (result.value as JsonObject).also { services.galleries.put(key.toString(), it) }
        }
        val db = services.database()
        val settings = ReaderSettingsRepository(db).get(key)
        val progress = ProgressRepository(db).get(key)
        val provider = OnlinePageProvider(s, sourceId, services.prefs.value.sourceSettings[sourceId], gallery, services.pageCache())
        HistoryRepository(db).touch(key, provider.title, provider.sizes.size, Instant.now())

        val webtoon = provider.tags.any { it.equals("webtoon", ignoreCase = true) }
        zoom = saved.get<Double>(KEY_ZOOM) ?: settings?.zoom ?: 1.0
        fitWidth = settings?.fitWidth ?: services.prefs.value.fitWidth
        gaps = settings?.gaps ?: !webtoon
        pendingPosition = when {
            saved.contains(KEY_PAGE) -> saved.get<Int>(KEY_PAGE)!! to (saved.get<Double>(KEY_OFFSET) ?: 0.0)
            startPage >= 0 -> startPage to 0.0
            progress != null && !progress.finished -> progress.page to progress.offset
            else -> 0 to 0.0
        }
        val created = ReaderSession(provider, maxOf(1.0, viewportWidth), viewModelScope, services.imageBudget)
        created.preloadPages = services.prefs.value.preloadPages
        created.onSizeChanged = { index, delta ->
            // A page above the view changed height: keep the content in view where it is.
            if (index < created.geometry.pageAt(scrollY)) scrollY = clampY(scrollY + delta)
        }
        session = created
        if (viewportWidth > 0) applyLayout(keepPosition = false)
    }

    /** The strip's size on screen (first layout, rotation, fold): the position stays (005 edge cases). */
    fun onViewport(width: Double, height: Double, density: Float) {
        if (width == viewportWidth && height == viewportHeight && density == this.density) return
        val position = session?.let { s -> pendingPosition ?: s.geometry.positionAt(scrollY) }
        viewportWidth = width
        viewportHeight = height
        this.density = density
        if (position != null) pendingPosition = position
        applyLayout(keepPosition = false)
    }

    private fun applyLayout(keepPosition: Boolean) {
        val s = session ?: return
        if (viewportWidth <= 0) return
        val position = if (keepPosition) s.geometry.positionAt(scrollY) else pendingPosition ?: s.geometry.positionAt(scrollY)
        s.setGap(if (gaps) services.prefs.value.pageGap * density.toDouble() else 0.0)
        s.setColumnWidth(columnWidth())
        scrollY = clampY(s.geometry.scrollFor(position.first, position.second))
        offsetX = offsetX.coerceIn(0.0, maxX())
        pendingPosition = null
        s.updateView(scrollY, viewportHeight)
    }

    private fun columnWidth() = StripGeometry.columnWidthFor(viewportWidth, services.prefs.value.readingWidth * density.toDouble(), zoom, fitWidth)

    /** Scrolls by [dy] px; returns the part consumed (the rest overscrolls). */
    fun scrollBy(dy: Double): Double {
        val s = session ?: return 0.0
        val before = scrollY
        scrollY = clampY(scrollY + dy)
        if (scrollY != before) {
            s.updateView(scrollY, viewportHeight)
            scheduleSave()
        }
        return scrollY - before
    }

    fun panBy(dx: Double): Double {
        val before = offsetX
        offsetX = (offsetX + dx).coerceIn(0.0, maxX())
        return offsetX - before
    }

    /** Pinch: the strip width changes around the point under the fingers (005 FR-015, 007 FR-012). */
    fun zoomAt(factor: Double, focusX: Double, focusY: Double) {
        val s = session ?: return
        val next = (zoom * factor).coerceIn(MIN_ZOOM, MAX_ZOOM)
        if (next == zoom) return
        val (page, fraction) = s.geometry.positionAt(scrollY + focusY)
        val oldWidth = s.geometry.columnWidth
        val fx = (offsetX + focusX - originX(oldWidth)) / oldWidth
        zoom = next
        s.setColumnWidth(columnWidth())
        val newWidth = s.geometry.columnWidth
        scrollY = clampY(s.geometry.scrollFor(page, fraction) - focusY)
        offsetX = if (newWidth > viewportWidth) (fx * newWidth - focusX).coerceIn(0.0, maxX()) else 0.0
        s.updateView(scrollY, viewportHeight)
        scheduleSave()
    }

    /** Back to the screen width (the "fit width" button). */
    fun fitToWidth() {
        zoom = 1.0
        fitWidth = true
        offsetX = 0.0
        applyLayout(keepPosition = true)
        scheduleSave()
    }

    /** True only at exactly the screen width (the button is lit then). */
    val atScreenWidth: Boolean get() = session?.let { kotlin.math.abs(it.geometry.columnWidth - viewportWidth) < 0.5 } ?: false

    fun toggleGaps() {
        gaps = !gaps
        applyLayout(keepPosition = true)
        scheduleSave()
    }

    fun jumpTo(page: Int) {
        val s = session ?: return
        scrollY = clampY(s.geometry.scrollFor(page, 0.0))
        s.updateView(scrollY, viewportHeight)
        scheduleSave()
    }

    /** Page at the top of the view (0-based). */
    val currentPage: Int get() = session?.geometry?.pageAt(scrollY + 1)?.coerceAtLeast(0) ?: 0
    val pageCount: Int get() = session?.pages?.size ?: 0

    /** Where the strip's left edge is: centred when narrower than the screen, else scrolled sideways. */
    fun originX(columnWidth: Double = session?.geometry?.columnWidth ?: viewportWidth): Double =
        if (columnWidth < viewportWidth) (viewportWidth - columnWidth) / 2 else -offsetX

    fun trimMemory() = session?.trimToVisible()

    private fun maxX() = maxOf(0.0, (session?.geometry?.columnWidth ?: 0.0) - viewportWidth)

    private fun clampY(y: Double): Double {
        val total = session?.geometry?.totalHeight ?: 0.0
        return y.coerceIn(0.0, maxOf(0.0, total - viewportHeight))
    }

    /** Saves the position 5 s after scrolling stops (005 FR-018). */
    private fun scheduleSave() {
        saveJob?.cancel()
        saveJob = viewModelScope.launch {
            delay(5_000)
            save()
        }
    }

    /** Saves now (leaving the reader, the app going to the background). */
    fun saveNow() {
        saveJob?.cancel()
        viewModelScope.launch { withContext(NonCancellable) { save() } }
    }

    private suspend fun save() {
        val s = session ?: return
        val (page, offset) = s.geometry.positionAt(scrollY)
        saved[KEY_PAGE] = page
        saved[KEY_OFFSET] = offset
        saved[KEY_ZOOM] = zoom
        val finished = scrollY + viewportHeight >= s.geometry.totalHeight - 1
        val db = services.database()
        ProgressRepository(db).set(key, Progress(page, offset, s.pages.size, finished, Instant.now()))
        ReaderSettingsRepository(db).set(key, ReaderSettings(zoom, fitWidth, gaps, Instant.now()))
    }

    override fun onCleared() {
        session?.let { s ->
            val (page, offset) = s.geometry.positionAt(scrollY)
            val finished = scrollY + viewportHeight >= s.geometry.totalHeight - 1
            val count = s.pages.size
            services.scope.launch {
                val db = services.database()
                ProgressRepository(db).set(key, Progress(page, offset, count, finished, Instant.now()))
                ReaderSettingsRepository(db).set(key, ReaderSettings(zoom, fitWidth, gaps, Instant.now()))
            }
            s.close()
        }
    }

    private companion object {
        const val MIN_ZOOM = 0.5
        const val MAX_ZOOM = 3.0
        const val KEY_PAGE = "page"
        const val KEY_OFFSET = "offset"
        const val KEY_ZOOM = "zoom"
    }
}
