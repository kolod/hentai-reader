package hentaireader.app.system

import android.app.Activity
import android.os.Build
import android.view.WindowManager
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

/**
 * Full screen only (007 FR-009, FR-010): every screen hides the status and navigation bars; a swipe
 * from an edge shows them over the content for a moment and the system hides them again. Page
 * content may use the cutout area; controls stay clear of it ([safeControls]).
 */
object Immersive {
    fun apply(activity: Activity) {
        val window = activity.window
        WindowCompat.setDecorFitsSystemWindows(window, false)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            window.attributes = window.attributes.apply {
                layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            }
        }
        hideBars(activity)
    }

    /** Called again on resume and on focus gain: dialogs and other apps may have shown the bars. */
    fun hideBars(activity: Activity) {
        WindowInsetsControllerCompat(activity.window, activity.window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
    }
}

/** Keeps controls and text out of the cutout and of any bars or keyboard that take space. */
@Composable
fun Modifier.safeControls(): Modifier = windowInsetsPadding(WindowInsets.safeDrawing.union(WindowInsets.displayCutout))
