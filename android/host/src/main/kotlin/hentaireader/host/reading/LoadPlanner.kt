package hentaireader.host.reading

/** Pages to load and keep decoded, in priority order. */
data class LoadPlan(val visible: List<Int>, val ahead: List<Int>, val behind: List<Int>) {
    /** Visible (top → bottom), then ahead (near → far), then behind (near → far). */
    val ordered: List<Int> get() = visible + ahead + behind

    operator fun contains(page: Int) = page in visible || page in ahead || page in behind

    companion object {
        val EMPTY = LoadPlan(emptyList(), emptyList(), emptyList())
    }
}

/**
 * The load and memory window (port of the Windows LoadPlanner; 005 FR-008, FR-008a): everything
 * visible, whatever the count; ahead at least N pages and one screen; behind at least one page and
 * half a screen. Sized by screen height, so many small pages in view still leave a full screen
 * loaded ahead.
 */
object LoadPlanner {
    fun plan(strip: StripGeometry, scrollY: Double, viewportHeight: Double, preloadPages: Int): LoadPlan {
        if (strip.count == 0 || viewportHeight <= 0) return LoadPlan.EMPTY
        val (first, last) = strip.pagesIn(scrollY, viewportHeight)
        val visible = (first..last).toList()

        val ahead = ArrayList<Int>()
        var covered = 0.0
        var i = last + 1
        while (i < strip.count && (ahead.size < preloadPages || covered < viewportHeight)) {
            ahead.add(i)
            covered += strip.heightOf(i)
            i++
        }

        val behind = ArrayList<Int>()
        covered = 0.0
        i = first - 1
        while (i >= 0 && (behind.size < 1 || covered < viewportHeight / 2)) {
            behind.add(i)
            covered += strip.heightOf(i)
            i--
        }
        return LoadPlan(visible, ahead, behind)
    }
}
