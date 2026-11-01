package hentaireader.host.reading

import kotlin.math.round
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.params.provider.ValueSource

/** Port of the Windows StripGeometryTests and LoadPlannerTests, plus the image budget. */
class StripAndPlanTest {
    private fun strip(column: Double, vararg sizes: Pair<Double, Double>) = StripGeometry(sizes.toList(), column)
    private fun strip(column: Double, count: Int, w: Double, h: Double) = StripGeometry(List(count) { w to h }, column)
    private fun r9(d: Double) = round(d * 1e9) / 1e9

    @Test
    fun `every page is column wide whatever its resolution`() {
        val s = strip(1000.0, 800.0 to 1131.0, 1280.0 to 1810.0, 2560.0 to 3620.0, 2560.0 to 1810.0, 1000.0 to 400.0)
        assertEquals(1000 * 1131 / 800.0, s.heightOf(0), 1e-6)
        assertEquals(1000 * 1810 / 1280.0, s.heightOf(1), 1e-6)
        assertEquals(s.heightOf(1), s.heightOf(2), 1e-6)
        assertEquals(1000 * 1810 / 2560.0, s.heightOf(3), 1e-6)
        assertEquals(400.0, s.heightOf(4), 1e-6)
    }

    @Test
    fun `pages touch and total is the sum`() {
        val s = strip(500.0, 2.0 to 3.0, 1.0 to 1.0, 4.0 to 1.0)
        assertEquals(listOf(0.0, 750.0, 1250.0), listOf(s.topOf(0), s.topOf(1), s.topOf(2)))
        assertEquals(1375.0, s.totalHeight)
    }

    @Test
    fun `unknown sizes default to portrait two by three`() {
        val s = strip(300.0, 0.0 to 0.0, Double.NaN to 5.0, -1.0 to 2.0)
        for (i in 0 until 3) assertEquals(450.0, s.heightOf(i), 1e-6)
    }

    @ParameterizedTest
    @CsvSource("0,0", "749.9,0", "750,1", "1374,2", "-10,0", "99999,2")
    fun `pageAt finds the page containing y`(y: Double, expected: Int) {
        assertEquals(expected, strip(500.0, 2.0 to 3.0, 1.0 to 1.0, 4.0 to 1.0).pageAt(y))
    }

    @Test
    fun `position survives width and zoom changes`() {
        val s = strip(1000.0, 2.0 to 3.0, 3.0 to 4.0, 16.0 to 9.0, 2.0 to 3.0)
        val (page, offset) = s.positionAt(s.topOf(2) + s.heightOf(2) * 0.25)
        assertEquals(2 to 0.25, page to r9(offset))
        s.setColumnWidth(StripGeometry.columnWidthFor(1400.0, 1000.0, 1.5))
        val moved = s.positionAt(s.scrollFor(page, offset))
        assertEquals(2 to 0.25, moved.first to r9(moved.second))
    }

    @Test
    fun `an exact size reports the delta and shifts only later pages`() {
        val s = strip(1000.0, 2.0 to 3.0, 2.0 to 3.0, 2.0 to 3.0)
        val topOfLast = s.topOf(2)
        assertEquals(500.0, s.setPageSize(1, 1000.0, 2000.0), 1e-6)
        assertEquals(1500.0, s.topOf(1))
        assertEquals(topOfLast + 500, s.topOf(2))
    }

    @Test
    fun `a correction above the view keeps the view still when the delta is applied`() {
        val s = strip(1000.0, 2.0 to 3.0, 2.0 to 3.0, 2.0 to 3.0, 2.0 to 3.0)
        val scroll = s.topOf(2) + 100
        val before = s.positionAt(scroll)
        val after = s.positionAt(scroll + s.setPageSize(0, 2.0, 4.0))
        assertEquals(before.first, after.first)
        assertEquals(before.second, after.second, 1e-9)
    }

    @ParameterizedTest
    @CsvSource("1920,1000,1,1000,false", "700,1000,1,700,false", "1920,1000,0.5,500,false", "1920,1000,2,2000,false",
        "1920,1000,1,1920,true", "700,1000,1,700,true", "1080,1000,0.5,540,true")
    fun `column width`(viewport: Double, reading: Double, zoom: Double, expected: Double, fit: Boolean) {
        assertEquals(expected, StripGeometry.columnWidthFor(viewport, reading, zoom, fit))
    }

    @Test
    fun `a gap separates pages without changing their heights`() {
        val s = strip(100.0, 1.0 to 1.0, 1.0 to 2.0, 1.0 to 1.0)
        s.setGap(10.0)
        assertEquals(listOf(0.0, 110.0, 320.0), listOf(s.topOf(0), s.topOf(1), s.topOf(2)))
        assertEquals(200.0, s.heightOf(1))
        assertEquals(420.0, s.totalHeight)
        assertEquals(0, s.pageAt(105.0))
        assertEquals(1, s.pageAt(110.0))
        assertEquals(100.0, s.setPageSize(0, 1.0, 2.0))
        assertEquals(420.0, s.topOf(2))
        s.setGap(0.0)
        assertEquals(500.0, s.totalHeight)
    }

    @Test
    fun `pagesIn spans the viewport`() {
        val s = strip(100.0, 10, 1.0, 1.0)
        assertEquals(2 to 4, s.pagesIn(250.0, 200.0))
        assertEquals(3 to 3, s.pagesIn(300.0, 100.0))
    }

    @Test
    fun `portrait screen with landscape pages loads all visible and a full screen ahead`() {
        val s = strip(1000.0, 100, 1920.0, 1080.0)
        val plan = LoadPlanner.plan(s, 0.0, 3840.0, preloadPages = 4)
        assertEquals((0 until 7).toList(), plan.visible)
        assertTrue(plan.ahead.size >= 4)
        assertTrue(plan.ahead.sumOf { s.heightOf(it) } >= 3840)
        assertEquals(plan.visible.last() + 1, plan.ahead.first())
    }

    @Test
    fun `tall pages need only the page count ahead`() {
        val plan = LoadPlanner.plan(strip(1000.0, 50, 1000.0, 3000.0), 0.0, 1000.0, 3)
        assertEquals(listOf(0), plan.visible)
        assertEquals(listOf(1, 2, 3), plan.ahead)
    }

    @ParameterizedTest
    @ValueSource(ints = [3, 4, 5])
    fun `the preload setting is the minimum page count`(n: Int) {
        assertEquals(n, LoadPlanner.plan(strip(1000.0, 50, 1000.0, 1500.0), 0.0, 1000.0, n).ahead.size)
    }

    @Test
    fun `behind is at least one page and half a screen, order is visible ahead behind`() {
        val s = strip(1000.0, 50, 1000.0, 200.0)
        assertEquals(listOf(19, 18, 17), LoadPlanner.plan(s, s.topOf(20), 1000.0, 4).behind)
        val t = strip(1000.0, 30, 1000.0, 1500.0)
        val plan = LoadPlanner.plan(t, t.topOf(10) + 10, 1000.0, 3)
        assertEquals(listOf(10, 11, 12, 13, 9), plan.ordered)
        assertTrue(9 in plan)
        assertFalse(8 in plan)
    }

    @Test
    fun `edges and empty strips`() {
        val s = strip(1000.0, 5, 1000.0, 1500.0)
        assertTrue(LoadPlanner.plan(s, s.totalHeight - 500, 1000.0, 4).ahead.isEmpty())
        assertTrue(LoadPlanner.plan(s, 0.0, 1000.0, 4).behind.isEmpty())
        assertSame(LoadPlan.EMPTY, LoadPlanner.plan(strip(1000.0, 0, 1.0, 1.0), 0.0, 1000.0, 4))
        assertSame(LoadPlan.EMPTY, LoadPlanner.plan(strip(1000.0, 3, 1.0, 1.0), 0.0, 0.0, 4))
    }

    // --- ImageBudget (resource-budgets.md) ---

    @Test
    fun `the budget follows the memory class within its bounds`() {
        val mib = 1024L * 1024
        assertEquals(64 * mib, ImageBudget.forMemoryClass(96).limitBytes)
        assertEquals(128 * mib, ImageBudget.forMemoryClass(256).limitBytes)
        assertEquals(192 * mib, ImageBudget.forMemoryClass(512).limitBytes)
        assertEquals(16 * mib, ImageBudget.forMemoryClass(256).maxEntryBytes)
    }

    @Test
    fun `release order is outside the plan, behind far first, further ahead, then minimum ahead, never visible`() {
        val s = strip(1000.0, 20, 1000.0, 500.0)            // 500 px pages, 1000 px screen
        val plan = LoadPlanner.plan(s, s.topOf(10), 1000.0, 5) // visible 10–11, ahead 12–16, behind 9
        val minimum = ImageBudget.minimumAhead(plan, s, 1000.0)
        assertEquals(setOf(12, 13, 14), minimum)           // one page plus one screen
        val held = (5..16).associateWith { 10L }            // 12 pages × 10 bytes
        val budget = ImageBudget(60)
        val out = budget.release(held, plan, minimum)
        assertEquals(listOf(5, 6, 7, 8), out.take(4).sorted()) // outside the plan first
        assertEquals(listOf(9, 16), out.drop(4))             // then behind, then the further preload, until it fits
        assertTrue(out.none { it in plan.visible })
        budget.shrink()
        assertEquals(30, budget.limitBytes)
        val tight = budget.release(held, plan, minimum)
        assertTrue(tight.none { it in plan.visible })
        assertTrue(14 in tight && 12 !in tight)              // minimum ahead goes far to near
    }
}
