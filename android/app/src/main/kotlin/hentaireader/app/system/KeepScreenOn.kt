package hentaireader.app.system

import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.platform.LocalView
import kotlinx.coroutines.delay

/**
 * Keeps the screen on while the reader is shown and was touched in the last 10 minutes (007 FR-014,
 * resource-budgets.md). [lastTouch] changes on every interaction; [enabled] is the setting.
 */
@Composable
fun KeepScreenOn(enabled: Boolean, lastTouch: Long) {
    val view = LocalView.current
    LaunchedEffect(enabled, lastTouch) {
        view.keepScreenOn = enabled
        if (enabled) {
            delay(IDLE_MS)
            view.keepScreenOn = false
        }
    }
    DisposableEffect(view) { onDispose { view.keepScreenOn = false } }
}

private const val IDLE_MS = 10 * 60 * 1000L
