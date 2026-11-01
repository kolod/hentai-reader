package hentaireader.app.ui.gate

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import hentaireader.app.R
import hentaireader.app.system.safeControls

/**
 * First-launch confirmation (003 US5, 007 FR-035): shown before any source request or any file of
 * the app's data is written; declining closes the app.
 */
@Composable
fun AdultGateScreen(onConfirm: () -> Unit, onDecline: () -> Unit) {
    Surface(Modifier.fillMaxSize()) {
        Column(
            Modifier.safeControls().verticalScroll(rememberScrollState()).padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(stringResource(R.string.Gate_Title), style = MaterialTheme.typography.headlineSmall)
            Text(stringResource(R.string.Gate_Body), style = MaterialTheme.typography.bodyLarge)
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp, androidx.compose.ui.Alignment.End)) {
                OutlinedButton(onClick = onDecline) { Text(stringResource(R.string.Gate_Decline)) }
                Button(onClick = onConfirm) { Text(stringResource(R.string.Gate_Confirm)) }
            }
        }
    }
}
