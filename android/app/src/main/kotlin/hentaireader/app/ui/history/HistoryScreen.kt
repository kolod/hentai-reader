package hentaireader.app.ui.history

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import hentaireader.app.AppServices
import hentaireader.app.R
import hentaireader.app.images.HistoryCovers
import hentaireader.app.ui.common.HrTopBar
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.data.HistoryEntry
import hentaireader.host.data.HistoryRepository
import kotlinx.coroutines.launch

/**
 * History (006 US2, 007 T069): every gallery opened in the reader, newest first, with cover,
 * progress and the time of last reading, loaded page by page; an entry opens at its saved position
 * (the saved copy when there is one); a long press removes it; Clear history after a confirmation.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun HistoryScreen(services: AppServices, covers: HistoryCovers, onOpen: (HistoryEntry) -> Unit) {
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    val entries = remember { mutableStateListOf<HistoryEntry>() }
    var end by remember { mutableStateOf(false) }
    var removing by remember { mutableStateOf<HistoryEntry?>(null) }
    var clearing by remember { mutableStateOf(false) }
    val list = rememberLazyListState()

    suspend fun reload() {
        val page = HistoryRepository(services.database()).page(0, maxOf(PAGE, entries.size))
        entries.clear()
        entries.addAll(page)
        end = page.size < maxOf(PAGE, entries.size)
    }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { scope.launch { reload() } }
    // Thousands of entries stay responsive: the next page loads near the end.
    LaunchedEffect(list) {
        snapshotFlow { list.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0 }.collect { last ->
            if (!end && entries.isNotEmpty() && last >= entries.size - 10) {
                val more = HistoryRepository(services.database()).page(entries.size, PAGE)
                entries.addAll(more)
                if (more.size < PAGE) end = true
            }
        }
    }

    Scaffold(
        topBar = {
            HrTopBar(stringResource(R.string.Nav_History)) {
                if (entries.isNotEmpty()) IconButton(onClick = { clearing = true }) { Icon(Icons.Filled.DeleteSweep, stringResource(R.string.History_Clear)) }
            }
        },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        if (entries.isEmpty()) {
            Box(Modifier.padding(padding).fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) { Text(stringResource(R.string.History_Empty)) }
            return@Scaffold
        }
        LazyColumn(Modifier.padding(padding).fillMaxSize(), state = list, contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            itemsIndexed(entries, key = { _, e -> e.key.toString() }) { _, entry ->
                Card(Modifier.fillMaxWidth().combinedClickable(onClick = { onOpen(entry) }, onLongClick = { removing = entry })) {
                    Row(Modifier.padding(8.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        val cover by produceState<ImageBitmap?>(null, entry.key, entry.coverFile) { value = covers.load(entry) }
                        Box(Modifier.width(64.dp).height(90.dp).background(MaterialTheme.colorScheme.surfaceVariant)) {
                            cover?.let { Image(it, null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop) }
                        }
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                            Text(entry.title, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            val p = entry.progress
                            Text(
                                when {
                                    p == null -> strings.plural("Gallery_PageCount", entry.pageCount.toLong())
                                    p.finished -> stringResource(R.string.Gallery_Finished)
                                    else -> stringResource(R.string.Gallery_Progress, strings.number(p.page + 1), strings.number(p.pageCount))
                                },
                                style = MaterialTheme.typography.bodySmall,
                            )
                            Text(listOfNotNull(entry.key.sourceId.takeIf { it != "local" }, strings.relative(entry.lastReadAt),
                                stringResource(R.string.Save_InLibrary).takeIf { entry.saved }).joinToString(" · "),
                                style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }

    removing?.let { entry ->
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text(entry.title, maxLines = 3, overflow = TextOverflow.Ellipsis) },
            confirmButton = {
                TextButton(onClick = {
                    removing = null
                    scope.launch {
                        covers.forget(HistoryRepository(services.database()).remove(entry.key))
                        entries.remove(entry)
                    }
                }) { Text(stringResource(R.string.History_Remove)) }
            },
            dismissButton = { TextButton(onClick = { removing = null }) { Text(stringResource(R.string.Reader_Cancel)) } },
        )
    }
    if (clearing) AlertDialog(
        onDismissRequest = { clearing = false },
        title = { Text(stringResource(R.string.History_ClearTitle)) },
        text = { Text(stringResource(R.string.History_ClearBody)) },
        confirmButton = {
            TextButton(onClick = {
                clearing = false
                scope.launch {
                    HistoryRepository(services.database()).clear().forEach(covers::forget)
                    entries.clear()
                }
            }) { Text(stringResource(R.string.History_Clear)) }
        },
        dismissButton = { TextButton(onClick = { clearing = false }) { Text(stringResource(R.string.Reader_Cancel)) } },
    )
}

private const val PAGE = 60
