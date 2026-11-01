package hentaireader.app.ui.common

import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import hentaireader.app.R

/** Opens the navigation pane; provided by the navigation shell to every screen except the reader. */
val LocalOpenMenu = staticCompositionLocalOf<() -> Unit> { {} }

/**
 * The top bar of every screen (007 FR-007): ☰ on a section's first screen, ← below it. Inset by
 * the safe area only (the system bars are hidden).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HrTopBar(
    title: String,
    onBack: (() -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
) {
    val openMenu = LocalOpenMenu.current
    TopAppBar(
        title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        navigationIcon = {
            if (onBack != null) {
                IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.Gallery_Back)) }
            } else {
                IconButton(onClick = openMenu) { Icon(Icons.Filled.Menu, contentDescription = stringResource(R.string.Nav_OpenMenu)) }
            }
        },
        actions = actions,
        windowInsets = WindowInsets.safeDrawing.union(WindowInsets.displayCutout).only(WindowInsetsSides.Top + WindowInsetsSides.Horizontal),
    )
}
