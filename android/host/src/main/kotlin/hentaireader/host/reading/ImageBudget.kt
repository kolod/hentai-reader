package hentaireader.host.reading

import kotlinx.coroutines.flow.MutableStateFlow

/**
 * The memory budget for decoded pages (007 FR-016, contracts/resource-budgets.md): the Windows
 * load window decides what should be decoded, and the budget decides what may stay. When it is
 * exceeded, pages are released farthest behind first, then farthest ahead beyond the minimum
 * preload (one page plus one screen), then the rest ahead; visible pages are never released.
 */
class ImageBudget(baseBytes: Long) {
    private val base = baseBytes
    var limitBytes: Long = baseBytes
        private set

    /** The largest single decode; a bigger page is decoded at a lower scale or in tiles. */
    val maxEntryBytes: Long get() = limitBytes / 8

    /** While the app is not shown, only the visible pages are kept (007 FR-017). */
    @Volatile var visibleOnly: Boolean = false
        private set

    /** Bumped on every change, so holders release what no longer fits. */
    val changes = MutableStateFlow(0)

    /** Halves the budget after a low-memory signal (until [restore]). */
    fun shrink(factor: Double = 0.5) {
        limitBytes = (base * factor).toLong()
        changes.value++
    }

    fun keepVisibleOnly() {
        visibleOnly = true
        changes.value++
    }

    /** The app is shown again: the full budget. */
    fun restore() {
        limitBytes = base
        visibleOnly = false
        changes.value++
    }

    /**
     * Which of the [held] pages (page → bytes) to release so the rest fits, given the current
     * [plan]. [minimumAhead] = pages ahead that cover one page and one screen (kept before the
     * further preload). Pages outside the plan go first in any case.
     */
    fun release(held: Map<Int, Long>, plan: LoadPlan, minimumAhead: Set<Int>): List<Int> {
        if (visibleOnly) return held.keys.filter { it !in plan.visible }
        val out = ArrayList<Int>()
        var total = held.values.sum()
        // Anything outside the plan is not needed at all.
        for ((page, bytes) in held) {
            if (page !in plan) {
                out.add(page)
                total -= bytes
            }
        }
        if (total <= limitBytes) return out
        val order = plan.behind.reversed() + plan.ahead.filter { it !in minimumAhead }.reversed() + plan.ahead.filter { it in minimumAhead }.reversed()
        for (page in order) {
            if (total <= limitBytes) break
            val bytes = held[page] ?: continue
            out.add(page)
            total -= bytes
        }
        return out
    }

    companion object {
        private const val MIB = 1024L * 1024

        /** clamp(memoryClass × 0.5 MiB, 64 MiB, 192 MiB) (resource-budgets.md). */
        fun forMemoryClass(memoryClassMb: Int) = ImageBudget((memoryClassMb * MIB / 2).coerceIn(64 * MIB, 192 * MIB))

        /** The pages ahead that cover one page plus one screen height. */
        fun minimumAhead(plan: LoadPlan, strip: StripGeometry, viewportHeight: Double): Set<Int> {
            val keep = HashSet<Int>()
            var covered = 0.0
            for ((i, page) in plan.ahead.withIndex()) {
                // The first page ahead, then pages until one more screen is covered.
                if (i > 1 && covered >= viewportHeight) break
                keep.add(page)
                if (i > 0) covered += strip.heightOf(page)
            }
            return keep
        }
    }
}
