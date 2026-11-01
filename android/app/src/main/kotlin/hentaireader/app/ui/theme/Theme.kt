package hentaireader.app.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// One fixed palette on every phone (no dynamic color), light and dark following the system.
private val Light = lightColorScheme(
    primary = Color(0xFF7A2E4F),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFFFD9E3),
    onPrimaryContainer = Color(0xFF3E0020),
    secondary = Color(0xFF74565F),
    background = Color(0xFFFBFAFC),
    surface = Color(0xFFFBFAFC),
)

private val Dark = darkColorScheme(
    primary = Color(0xFFFFB0C9),
    onPrimary = Color(0xFF5C1135),
    primaryContainer = Color(0xFF7A2E4F),
    onPrimaryContainer = Color(0xFFFFD9E3),
    secondary = Color(0xFFE2BDC6),
    background = Color(0xFF131214),
    surface = Color(0xFF131214),
)

@Composable
fun HentaiReaderTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) Dark else Light, content = content)
}
