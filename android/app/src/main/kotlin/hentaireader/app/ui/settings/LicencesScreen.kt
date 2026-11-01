package hentaireader.app.ui.settings

import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import hentaireader.app.R
import hentaireader.app.ui.common.HrTopBar
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** The third-party notices shipped in the APK (007 T084), shown as they are written. */
@Composable
fun LicencesScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val lines by produceState(emptyList<String>()) {
        value = withContext(Dispatchers.IO) { context.assets.open("THIRD-PARTY-NOTICES.md").bufferedReader().readLines() }
    }
    Scaffold(topBar = { HrTopBar(stringResource(R.string.Settings_Licences_Android), onBack = onBack) }, contentWindowInsets = WindowInsets(0)) { padding ->
        SelectionContainer {
            LazyColumn(Modifier.padding(padding).fillMaxSize(), contentPadding = PaddingValues(16.dp)) {
                items(lines) { line ->
                    Text(line, style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace))
                }
            }
        }
    }
}
