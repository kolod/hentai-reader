package hentaireader.app.ui.common

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import hentaireader.app.R
import hentaireader.host.engine.CallError
import kotlin.math.ceil

/** A failed source call as the UI shows it: kind, translated title and body, technical details. */
data class ErrorInfo(val kind: String, val retryAfterSeconds: Double?, val details: String) {
    companion object {
        /** Script errors and limit hits are shown as "the source failed" (003 FR-013, edge cases). */
        fun of(e: CallError, sourceName: String): ErrorInfo {
            val kind = if (e.type == "SourceError" && e.details?.get("limit") == null) e.kind ?: "ScriptError" else "ScriptError"
            return ErrorInfo(kind, e.retryAfterSeconds, "$sourceName: ${e.type}(${e.kind ?: e.name}): ${e.message}")
        }

        fun network(t: Throwable, sourceName: String) = ErrorInfo("Network", null, "$sourceName: ${t.javaClass.simpleName}: ${t.message}")
    }
}

/**
 * Per-kind error message with Retry and "copy details" (007 T038). After RateLimited the source
 * sends nothing until the user presses Retry (the session stays limited, 003 FR-013).
 */
@Composable
fun ErrorPanel(error: ErrorInfo, onRetry: (() -> Unit)?, modifier: Modifier = Modifier) {
    val strings = rememberStrings()
    val context = LocalContext.current
    var body = strings.errorBody(error.kind)
    if (error.kind == "RateLimited" && error.retryAfterSeconds != null) {
        body += " " + strings.get("Error_RateLimited_After", strings.number(maxOf(1, ceil(error.retryAfterSeconds / 60).toInt())))
    }
    Column(modifier.fillMaxWidth().padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(strings.errorTitle(error.kind), style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
        Text(body, style = MaterialTheme.typography.bodyMedium, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp, androidx.compose.ui.Alignment.CenterHorizontally)) {
            TextButton(onClick = {
                val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                clipboard.setPrimaryClip(ClipData.newPlainText("error", error.details))
            }) { Text(stringResource(R.string.Search_CopyDetails)) }
            if (onRetry != null) Button(onClick = onRetry) { Text(stringResource(R.string.Search_Retry)) }
        }
    }
}
