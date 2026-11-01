package hentaireader.app.ui.reader

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.rememberScrollableState
import androidx.compose.foundation.gestures.scrollable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberOverscrollEffect
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import hentaireader.app.R
import kotlin.math.roundToInt

/**
 * The seamless strip (005 FR-004 – FR-010, 007 FR-011 – FR-013; research R7): one canvas draws the
 * visible slots from the geometry, a loaded page as its bitmap (or its tiles), a page not loaded
 * yet as a placeholder of its reserved size, so nothing moves while images arrive. One finger
 * scrolls and flings, two fingers zoom around their centre, sideways drags pan a strip wider than
 * the screen, a tap shows or hides the toolbar. Only failed and loading pages in view get small
 * composables on top.
 */
@Composable
fun StripCanvas(vm: ReaderViewModel, session: ReaderSession, onTap: () -> Unit, modifier: Modifier = Modifier) {
    val density = LocalDensity.current.density
    val placeholder = MaterialTheme.colorScheme.surfaceVariant
    // A drag down (positive delta) moves the content down, i.e. scrolls back.
    val vertical = rememberScrollableState { delta -> -vm.scrollBy(-delta.toDouble()).toFloat() }
    val horizontal = rememberScrollableState { delta -> -vm.panBy(-delta.toDouble()).toFloat() }

    Box(
        modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .onSizeChanged { vm.onViewport(it.width.toDouble(), it.height.toDouble(), density) }
            .pointerInput(vm) {
                // Two or more fingers: zoom around their centroid; the scrollables never see it.
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    do {
                        val event = awaitPointerEvent(PointerEventPass.Initial)
                        if (event.changes.count { it.pressed } >= 2) {
                            val zoom = event.calculateZoom()
                            val centroid = event.calculateCentroid(useCurrent = true)
                            if (zoom != 1f && centroid != Offset.Unspecified) vm.zoomAt(zoom.toDouble(), centroid.x.toDouble(), centroid.y.toDouble())
                            event.changes.forEach { it.consume() }
                        }
                    } while (event.changes.any { it.pressed })
                }
            }
            .pointerInput(vm) { detectTapGestures(onTap = { onTap() }) }
            .scrollable(vertical, Orientation.Vertical, overscrollEffect = rememberOverscrollEffect())
            .scrollable(horizontal, Orientation.Horizontal),
    ) {
        Canvas(Modifier.fillMaxSize()) {
            @Suppress("UNUSED_VARIABLE") val version = session.layoutVersion
            val g = session.geometry
            if (g.count == 0) return@Canvas
            val y = vm.scrollY
            val x = vm.originX().toFloat()
            val w = g.columnWidth.toFloat()
            val (first, last) = g.pagesIn(y, size.height.toDouble())
            for (i in first..last) {
                val top = (g.topOf(i) - y).toFloat()
                val h = g.heightOf(i).toFloat()
                val page = session.pages[i]
                val image = page.image
                if (image != null) {
                    drawImage(image, dstOffset = IntOffset(x.roundToInt(), top.roundToInt()),
                        dstSize = IntSize(w.roundToInt(), h.roundToInt()), filterQuality = FilterQuality.Medium)
                    continue
                }
                drawRect(placeholder, Offset(x, top), Size(w, h))
                if (page.tileCount > 0) {
                    for ((t, tile) in page.tiles) {
                        val t0 = top + h * t / page.tileCount
                        val t1 = top + h * (t + 1) / page.tileCount
                        drawImage(tile, dstOffset = IntOffset(x.roundToInt(), t0.roundToInt()),
                            dstSize = IntSize(w.roundToInt(), (t1 - t0).roundToInt() + 1), filterQuality = FilterQuality.Medium)
                    }
                }
            }
        }
        Overlays(vm, session, density)
    }
}

/** Retry for failed pages and a spinner for loading ones, each centred in its visible slot. */
@Composable
private fun Overlays(vm: ReaderViewModel, session: ReaderSession, density: Float) {
    @Suppress("UNUSED_VARIABLE") val version = session.layoutVersion
    val g = session.geometry
    if (g.count == 0 || vm.viewportHeight <= 0) return
    val (first, last) = g.pagesIn(vm.scrollY, vm.viewportHeight)
    for (i in first..last) {
        val page = session.pages[i]
        if (page.image != null || page.tiles.isNotEmpty()) continue
        val top = maxOf(0.0, g.topOf(i) - vm.scrollY)
        val bottom = minOf(vm.viewportHeight, g.topOf(i) + g.heightOf(i) - vm.scrollY)
        val left = maxOf(0.0, vm.originX())
        val width = minOf(vm.viewportWidth, g.columnWidth)
        if (bottom - top < 48 * density) continue
        Box(
            Modifier.offset { IntOffset(left.roundToInt(), top.roundToInt()) }
                .size(((width) / density).dp, ((bottom - top) / density).dp),
            contentAlignment = Alignment.Center,
        ) {
            when (page.load) {
                PageLoad.FAILED -> Column(Modifier.padding(16.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.Reader_PageFailed), textAlign = TextAlign.Center)
                    Button(onClick = { session.retry(page) }) { Text(stringResource(R.string.Search_Retry)) }
                }
                PageLoad.PLACEHOLDER -> Unit
                else -> CircularProgressIndicator()
            }
        }
    }
}
