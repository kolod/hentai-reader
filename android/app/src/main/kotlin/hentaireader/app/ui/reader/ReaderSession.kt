package hentaireader.app.ui.reader

import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import hentaireader.app.images.DecodedPage
import hentaireader.app.images.PageDecoder
import hentaireader.host.engine.SourceErrorKind
import hentaireader.host.http.ImageHttpException
import hentaireader.host.reading.ImageBudget
import hentaireader.host.reading.LoadPlan
import hentaireader.host.reading.LoadPlanner
import hentaireader.host.reading.PageAttempts
import hentaireader.host.reading.PageProvider
import hentaireader.host.reading.PageResolveException
import hentaireader.host.reading.StripGeometry
import hentaireader.host.sources.SourceLimitedException
import kotlin.math.abs
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext

private const val TAG = "ReaderSession"

enum class PageLoad { PLACEHOLDER, RESOLVING, DOWNLOADING, DECODING, LOADED, FAILED }

/** One page of the strip as the reader shows it. */
class PageState(val index: Int) {
    val attempts = PageAttempts()
    var load by mutableStateOf(PageLoad.PLACEHOLDER)
    var error by mutableStateOf<String?>(null)
    var image by mutableStateOf<ImageBitmap?>(null)
    var tiled: DecodedPage.Tiled? = null
    val tiles = mutableStateMapOf<Int, ImageBitmap>()
    var tileCount = 0
    var decodedWidth = 0
    var naturalWidth = 0

    val bytes: Long
        get() = (image?.let { it.width.toLong() * it.height * 4 } ?: 0L) + tiles.values.sumOf { it.width.toLong() * it.height * 4 }
}

/**
 * Loads the pages of one gallery for the reader (port of the Windows ReaderSession; 005 research
 * R4, R5, R7): keeps the load window of [LoadPlanner] loaded and decoded, everything else released,
 * and within the [budget] (007 FR-016). Exact sizes learned before a download are applied to the
 * geometry before decoding; [onSizeChanged] lets the strip keep the view still. Runs on the main
 * thread; script calls, network, file reads and decoding run elsewhere.
 */
class ReaderSession(
    val provider: PageProvider,
    columnWidth: Double,
    private val scope: CoroutineScope,
    private val budget: ImageBudget,
) {
    val geometry = StripGeometry(provider.sizes, columnWidth)
    val pages = List(provider.sizes.size) { PageState(it) }

    // A low-memory signal releases what no longer fits at once (007 FR-017); when the budget
    // comes back, the pages ahead load again from the cache.
    private val budgetWatch = scope.launch {
        budget.changes.drop(1).collect { if (plan != LoadPlan.EMPTY && !budget.visibleOnly) updateView(scrollY, viewport) else enforceBudget() }
    }

    /** Pages to keep ahead (preload setting, 3–5). */
    var preloadPages = 3

    /** Bumped when the geometry changes, so the strip redraws. */
    var layoutVersion by mutableIntStateOf(0)
        private set

    /** Non-null while the source is limited: the retry delay in seconds (or NaN when unknown). */
    var limitedFor by mutableStateOf<Double?>(null)
        private set

    /** A page's height changed (exact size learned): page, height delta. */
    var onSizeChanged: ((Int, Double) -> Unit)? = null

    private val slots = Semaphore(provider.concurrency)
    private val loading = HashMap<Int, Job>()
    private val tileJobs = HashMap<Pair<Int, Int>, Job>()
    private var plan = LoadPlan.EMPTY
    private var scrollY = 0.0
    private var viewport = 1.0
    private var closed = false

    /** Re-plans for the current view: starts loads in priority order, releases the rest. */
    fun updateView(scrollY: Double, viewportHeight: Double) {
        if (closed) return
        this.scrollY = scrollY
        this.viewport = viewportHeight
        plan = LoadPlanner.plan(geometry, scrollY, viewportHeight, preloadPages)

        for ((page, job) in loading.filterKeys { it !in plan }) {
            job.cancel()
            loading.remove(page)
            if (pages[page].load != PageLoad.FAILED) pages[page].load = PageLoad.PLACEHOLDER
        }
        enforceBudget()
        updateTiles()
        if (provider.isLimited) return
        for (index in plan.ordered) {
            val page = pages[index]
            if (page.load == PageLoad.PLACEHOLDER && index !in loading) start(page)
        }
    }

    /** The column width changed (screen, reading width, zoom): images far from the new width are decoded again. */
    fun setColumnWidth(width: Double) {
        if (width == geometry.columnWidth) return
        geometry.setColumnWidth(width)
        layoutVersion++
        for (page in pages) {
            if (page.image != null && abs(page.decodedWidth - decodeWidth(page)) > page.decodedWidth * 0.2) release(page)
            if (page.tiled != null) clearTiles(page)
        }
    }

    fun setGap(gap: Double) {
        if (gap == geometry.gap) return
        geometry.setGap(gap)
        layoutVersion++
    }

    /** Retry of one failed page: the source is asked for a new location with a RetryHint. */
    fun retry(page: PageState) {
        if (page.load != PageLoad.FAILED) return
        page.error = null
        page.load = PageLoad.PLACEHOLDER
        if (page.index in plan && !provider.isLimited) start(page)
    }

    /** The user chose Retry after a limit: requests resume (005 FR-021). */
    fun retryAfterLimit() {
        provider.clearLimit()
        limitedFor = null
        for (page in pages) if (page.load == PageLoad.FAILED) page.load = PageLoad.PLACEHOLDER
        updateView(scrollY, viewport)
    }

    fun close() {
        closed = true
        budgetWatch.cancel()
        loading.values.forEach { it.cancel() }
        loading.clear()
        tileJobs.values.forEach { it.cancel() }
        tileJobs.clear()
        pages.forEach { release(it) }
        provider.close()
    }

    private fun start(page: PageState) {
        lateinit var job: Job
        job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                val bytes = slots.withPermit {
                    page.load = PageLoad.RESOLVING
                    provider.load(page.index, page.attempts) { w, h ->
                        if (w != null && h != null && w > 0 && h > 0) applySize(page.index, w, h)
                        page.load = PageLoad.DOWNLOADING
                    }
                }
                page.load = PageLoad.DECODING
                val width = decodeWidth(page)
                val decoded = withContext(Dispatchers.Default) { PageDecoder.decode(bytes, width, budget.maxEntryBytes) }
                // The image itself is the final word on proportions (cached pages never saw a PageImage size).
                applySize(page.index, decoded.sourceWidth.toDouble(), decoded.sourceHeight.toDouble())
                page.naturalWidth = decoded.sourceWidth
                page.decodedWidth = width
                when (decoded) {
                    is DecodedPage.Whole -> page.image = decoded.bitmap.asImageBitmap()
                    is DecodedPage.Tiled -> page.tiled = decoded
                }
                page.attempts.attempts = 0
                page.attempts.failedStatus = null
                page.load = PageLoad.LOADED
                enforceBudget()
                updateTiles()
            } catch (e: CancellationException) {
                throw e
            } catch (e: PageResolveException) {
                Log.w(TAG, "page ${page.index + 1}: ${e.error}")
                fail(page, if (e.error.type == "SourceError") e.error.kind ?: "ScriptError" else "ScriptError")
                if (e.error.kind == SourceErrorKind.RATE_LIMITED) limitedFor = e.error.retryAfterSeconds ?: Double.NaN
            } catch (e: SourceLimitedException) {
                fail(page, "RateLimited")
                limitedFor = e.retryAfterSeconds ?: Double.NaN
            } catch (e: ImageHttpException) {
                Log.w(TAG, "page ${page.index + 1}: ${e.message}")
                page.attempts.failedStatus = e.status
                fail(page, "Network")
            } catch (e: Exception) {
                Log.w(TAG, "page ${page.index + 1} failed", e)
                fail(page, "Network")
            } finally {
                if (loading[page.index] === job) loading.remove(page.index)
            }
        }
        loading[page.index] = job
        job.start()
    }

    /** Tiles of tiled pages near the view are decoded; the others are released. */
    private fun updateTiles() {
        val tileHeight = viewport / 2
        val top = scrollY - viewport / 2
        val bottom = scrollY + viewport * 1.5
        val wanted = HashSet<Pair<Int, Int>>()
        for (page in pages) {
            val tiled = page.tiled ?: continue
            if (page.index !in plan) {
                clearTiles(page)
                continue
            }
            val height = geometry.heightOf(page.index)
            val count = PageDecoder.tileCount(height, tileHeight)
            if (count != page.tileCount) {
                clearTiles(page)
                page.tileCount = count
            }
            val pageTop = geometry.topOf(page.index)
            for (t in 0 until count) {
                val t0 = pageTop + height * t / count
                val t1 = pageTop + height * (t + 1) / count
                if (t1 < top || t0 > bottom) {
                    page.tiles.remove(t)
                    continue
                }
                val key = page.index to t
                wanted.add(key)
                if (t in page.tiles || key in tileJobs) continue
                val width = decodeWidth(page)
                tileJobs[key] = scope.launch {
                    try {
                        val bitmap = withContext(Dispatchers.Default) {
                            PageDecoder.tile(tiled, tiled.sourceHeight * t / count, tiled.sourceHeight * (t + 1) / count, width)
                        }
                        if (bitmap != null && page.tiled === tiled && page.tileCount == count) page.tiles[t] = bitmap.asImageBitmap()
                    } catch (e: CancellationException) {
                        throw e
                    } catch (_: Exception) {
                        // Out of memory or a broken image: the tile stays a placeholder.
                    } finally {
                        tileJobs.remove(key)
                    }
                }
            }
        }
        for ((key, job) in tileJobs.filterKeys { it !in wanted }) {
            job.cancel()
            tileJobs.remove(key)
        }
    }

    private fun enforceBudget() {
        val held = pages.filter { it.bytes > 0 }.associate { it.index to it.bytes }
        if (held.isEmpty()) return
        val minimum = ImageBudget.minimumAhead(plan, geometry, viewport)
        for (index in budget.release(held, plan, minimum)) release(pages[index])
    }

    private fun applySize(index: Int, width: Double, height: Double) {
        val before = geometry.heightOf(index)
        val expected = geometry.columnWidth * height / width
        if (abs(expected - before) < 0.5) return
        val delta = geometry.setPageSize(index, width, height)
        layoutVersion++
        onSizeChanged?.invoke(index, delta)
    }

    /** Decode width for the current column, never wider than the image itself. */
    private fun decodeWidth(page: PageState): Int {
        var width = geometry.columnWidth
        if (page.naturalWidth > 0) width = minOf(width, page.naturalWidth.toDouble())
        return maxOf(1, width.toInt())
    }

    private fun fail(page: PageState, kind: String) {
        page.error = kind
        page.load = PageLoad.FAILED
    }

    private fun clearTiles(page: PageState) {
        page.tiles.clear()
        page.tileCount = 0
    }

    private fun release(page: PageState) {
        page.image = null
        page.tiled?.decoder?.recycle()
        page.tiled = null
        clearTiles(page)
        page.decodedWidth = 0
        if (page.load == PageLoad.LOADED || page.load == PageLoad.DECODING) page.load = PageLoad.PLACEHOLDER
    }
}
