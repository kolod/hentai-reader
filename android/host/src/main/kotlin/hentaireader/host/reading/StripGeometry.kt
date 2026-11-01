package hentaireader.host.reading

/**
 * Layout of the reader strip (port of the Windows StripGeometry; 005 data-model "Strip
 * geometry"): every page is exactly the column width, whatever its resolution (005 FR-004a); its
 * height follows its own aspect ratio; pages touch unless the reader asks for a gap between them.
 * Units are whatever the caller uses for the column width (pixels on Android).
 */
class StripGeometry(sizes: List<Pair<Double, Double>>, columnWidth: Double) {
    private val width = DoubleArray(sizes.size)
    private val height = DoubleArray(sizes.size)
    private val tops = DoubleArray(sizes.size + 1)

    val count: Int get() = width.size
    var columnWidth: Double = columnWidth
        private set

    /** Space between neighbouring pages (0 = seamless). */
    var gap: Double = 0.0
        private set
    val totalHeight: Double get() = tops[count]

    init {
        require(columnWidth > 0) { "column width must be positive" }
        sizes.forEachIndexed { i, (w, h) -> normalize(w, h).let { (nw, nh) -> width[i] = nw; height[i] = nh } }
        recompute(0)
    }

    fun topOf(page: Int): Double = tops[page]
    fun heightOf(page: Int): Double = columnWidth * height[page] / width[page]

    /** Aspect of a page (height / width). */
    fun aspectOf(page: Int): Double = height[page] / width[page]

    fun setColumnWidth(value: Double) {
        require(value > 0) { "column width must be positive" }
        if (value == columnWidth) return
        columnWidth = value
        recompute(0)
    }

    fun setGap(value: Double) {
        require(value >= 0 && value.isFinite()) { "gap must be finite and not negative" }
        if (value == gap) return
        gap = value
        recompute(0)
    }

    /**
     * Replaces a page's (estimated) size with its exact size. Returns the change of that page's
     * height; the caller keeps the view still when the page lies above the viewport (005 FR-006).
     */
    fun setPageSize(page: Int, w: Double, h: Double): Double {
        val before = heightOf(page)
        normalize(w, h).let { (nw, nh) -> width[page] = nw; height[page] = nh }
        recompute(page)
        return heightOf(page) - before
    }

    /** Page whose span contains [y] (clamped to the strip); a gap belongs to the page above it. */
    fun pageAt(y: Double): Int {
        if (count == 0) return -1
        if (y <= 0) return 0
        if (y >= totalHeight) return count - 1
        var lo = 0
        var hi = count - 1
        while (lo < hi) {
            val mid = (lo + hi + 1) / 2
            if (tops[mid] <= y) lo = mid else hi = mid - 1
        }
        return lo
    }

    /** Page at [y] and the fraction (0–1) of its height above y. */
    fun positionAt(y: Double): Pair<Int, Double> {
        val page = pageAt(y)
        if (page < 0) return 0 to 0.0
        val h = heightOf(page)
        return page to if (h > 0) ((y - tops[page]) / h).coerceIn(0.0, 1.0) else 0.0
    }

    /** Scroll offset that puts the given position at the top of the view. */
    fun scrollFor(page: Int, offset: Double): Double {
        if (count == 0) return 0.0
        val p = page.coerceIn(0, count - 1)
        return tops[p] + offset.coerceIn(0.0, 1.0) * heightOf(p)
    }

    /** First and last page intersecting [y, y + viewport); (-1, -1) for an empty strip. */
    fun pagesIn(y: Double, viewport: Double): Pair<Int, Int> {
        if (count == 0) return -1 to -1
        return pageAt(y) to pageAt(maxOf(y, y + viewport - 0.001))
    }

    private fun recompute(from: Int) {
        for (i in from until count) tops[i + 1] = tops[i] + heightOf(i) + if (i < count - 1) gap else 0.0
    }

    companion object {
        /** Aspect used when a source gives no usable size: portrait 2:3. */
        const val DEFAULT_WIDTH = 2.0
        const val DEFAULT_HEIGHT = 3.0

        /**
         * Column width for a viewport times zoom: the viewport width when fitting the screen,
         * otherwise the reading width (narrower screens use their width).
         */
        fun columnWidthFor(viewportWidth: Double, readingWidth: Double, zoom: Double, fitWidth: Boolean = false): Double =
            maxOf(1.0, if (fitWidth) viewportWidth else minOf(viewportWidth, readingWidth)) * zoom

        private fun normalize(w: Double, h: Double): Pair<Double, Double> =
            if (w > 0 && h > 0 && w.isFinite() && h.isFinite()) w to h else DEFAULT_WIDTH to DEFAULT_HEIGHT
    }
}
