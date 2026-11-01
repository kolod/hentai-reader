package hentaireader.app.ui.downloads

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.app.NotificationManagerCompat
import hentaireader.app.AppServices
import hentaireader.app.R
import hentaireader.app.downloads.DownloadService
import hentaireader.app.system.Notifications
import hentaireader.app.ui.common.HrTopBar
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.data.DownloadRow
import hentaireader.host.data.DownloadState
import kotlinx.coroutines.launch

/**
 * The download list (006 FR-004, 007 T064): state, progress and the reason of a stop (a limit,
 * waiting for Wi-Fi, stopped by the system, failed); Pause / Resume / Cancel per download; a
 * banner when notifications are off (downloads then run only while the app is open).
 */
@Composable
fun DownloadsScreen(services: AppServices) {
    val context = LocalContext.current
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    var refresh by remember { mutableIntStateOf(0) }
    val rows by produceState(emptyList<DownloadRow>(), refresh) { value = services.downloadRepository().list() }
    LaunchedEffect(Unit) { services.downloads().changes.collect { refresh++ } }
    val notificationsOff = !NotificationManagerCompat.from(context).areNotificationsEnabled()

    Scaffold(
        topBar = {
            HrTopBar(stringResource(R.string.Nav_Downloads)) {
                if (rows.any { it.state == DownloadState.DONE }) {
                    TextButton(onClick = { scope.launch { services.downloadRepository().deleteDone(); refresh++ } }) {
                        Text(stringResource(R.string.Downloads_ClearDone))
                    }
                }
            }
        },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        if (rows.isEmpty()) {
            Box(Modifier.padding(padding).fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) { Text(stringResource(R.string.Downloads_Empty)) }
            return@Scaffold
        }
        LazyColumn(Modifier.padding(padding).fillMaxSize(), contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (notificationsOff) item {
                Text(stringResource(R.string.Downloads_NotificationsOff), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(4.dp))
            }
            items(rows, key = { it.id }) { row ->
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(row.title, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
                                Text(stateText(row, strings), style = MaterialTheme.typography.bodySmall)
                            }
                            when (row.state) {
                                DownloadState.QUEUED, DownloadState.DOWNLOADING -> IconButton(onClick = { scope.launch { services.downloads().pause(row.id); refresh++ } }) {
                                    Icon(Icons.Filled.Pause, stringResource(R.string.Downloads_Pause))
                                }
                                DownloadState.PAUSED, DownloadState.LIMITED, DownloadState.FAILED -> IconButton(onClick = {
                                    scope.launch { services.downloads().resume(row.id); DownloadService.start(context); refresh++ }
                                }) { Icon(Icons.Filled.PlayArrow, stringResource(R.string.Downloads_Resume)) }
                                else -> Unit
                            }
                            if (row.state != DownloadState.DONE) IconButton(onClick = { scope.launch { services.downloads().cancel(row.id); refresh++ } }) {
                                Icon(Icons.Filled.Close, stringResource(R.string.Save_Cancel))
                            }
                        }
                        if (row.state != DownloadState.DONE && row.pageCount > 0) {
                            LinearProgressIndicator(progress = { row.pagesDone.toFloat() / row.pageCount }, modifier = Modifier.fillMaxWidth())
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun stateText(row: DownloadRow, strings: hentaireader.app.ui.common.Strings): String {
    val context = LocalContext.current
    val n = strings.number(row.pagesDone)
    val total = strings.number(row.pageCount)
    row.errorKind?.let { reason -> Notifications.reasonText(context, reason)?.let { return it } }
    return when (row.state) {
        DownloadState.QUEUED -> stringResource(R.string.Save_Queued)
        DownloadState.DOWNLOADING, DownloadState.ASSEMBLING -> stringResource(R.string.Save_Saving, n, total)
        DownloadState.PAUSED -> stringResource(R.string.Downloads_Paused, n, total)
        DownloadState.LIMITED -> if (row.errorKind == "LoginRequired") stringResource(R.string.Save_LoginRequired) else stringResource(R.string.Save_Limited)
        DownloadState.FAILED -> stringResource(R.string.Downloads_Failed, row.errorMessage ?: row.errorKind ?: "")
        DownloadState.DONE -> stringResource(R.string.Save_InLibrary)
    }
}
