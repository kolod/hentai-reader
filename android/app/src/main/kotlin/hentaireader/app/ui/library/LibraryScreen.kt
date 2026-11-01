package hentaireader.app.ui.library

import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import hentaireader.app.AppServices
import hentaireader.app.R
import hentaireader.app.ui.common.HrTopBar
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveState
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.LibraryItem
import hentaireader.host.data.LibraryQuery
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Saved galleries (006 US1/US3, 007 T065): cover, title, source, pages, progress and folder; opens
 * offline in the reader; a long press deletes the archive from the device after a confirmation
 * (there is no recycle bin), keeping its history. Search, filters and sorts come with US5.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun LibraryScreen(services: AppServices, onOpen: (LibraryItem) -> Unit) {
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    var refresh by remember { mutableIntStateOf(0) }
    var deleting by remember { mutableStateOf<LibraryItem?>(null) }
    val items by produceState<List<LibraryItem>?>(null, refresh) { value = ArchiveRepository(services.database()).list(LibraryQuery()) }
    LaunchedEffect(Unit) { services.downloads().changes.collect { refresh++ } }

    Scaffold(topBar = { HrTopBar(stringResource(R.string.Nav_Library)) }, contentWindowInsets = WindowInsets(0)) { padding ->
        val list = items ?: return@Scaffold
        if (list.isEmpty()) {
            Box(Modifier.padding(padding).fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) { Text(stringResource(R.string.Library_Empty)) }
            return@Scaffold
        }
        LazyVerticalGrid(
            columns = GridCells.Adaptive(150.dp),
            modifier = Modifier.padding(padding).fillMaxSize(),
            contentPadding = PaddingValues(12.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            items(list, key = { it.archive.id }) { item ->
                val available = item.archive.state == ArchiveState.OK
                Card(Modifier.fillMaxWidth().alpha(if (available) 1f else 0.5f).combinedClickable(
                    enabled = true,
                    onClick = { if (available) onOpen(item) },
                    onLongClick = { deleting = item },
                )) {
                    ArchiveCover(services, item, Modifier.fillMaxWidth().aspectRatio(0.7f))
                    Column(Modifier.padding(8.dp)) {
                        Text(item.archive.title, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        val progress = item.progress
                        val meta = listOfNotNull(
                            item.archive.key.sourceId.takeIf { it != "local" },
                            strings.plural("Gallery_PageCount", item.archive.pages.size.toLong()),
                            progress?.let { if (it.finished) stringResource(R.string.Gallery_Finished) else stringResource(R.string.Gallery_Progress, strings.number(it.page + 1), strings.number(it.pageCount)) },
                        ).joinToString(" · ")
                        Text(meta, style = MaterialTheme.typography.labelSmall, maxLines = 2)
                        when (item.archive.state) {
                            ArchiveState.UNAVAILABLE -> Text(stringResource(R.string.Library_Unavailable), style = MaterialTheme.typography.labelSmall)
                            ArchiveState.UNREADABLE -> Text(stringResource(R.string.Library_Unreadable), style = MaterialTheme.typography.labelSmall)
                            ArchiveState.OK -> Unit
                        }
                    }
                }
            }
        }
    }

    deleting?.let { item ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { Text(stringResource(R.string.Library_DeleteTitle)) },
            text = { Text(item.archive.title + "\n\n" + stringResource(R.string.Library_DeleteFromDevice_Body)) },
            confirmButton = {
                TextButton(onClick = {
                    deleting = null
                    scope.launch {
                        val db = services.database()
                        val folder = FolderRepository(db).list().firstOrNull { it.id == item.archive.folderId }
                        val deleted = withContext(Dispatchers.IO) { folder != null && services.tree(folder).delete(item.archive.relPath) }
                        if (deleted || folder == null) ArchiveRepository(db).delete(item.archive.id)
                        refresh++
                    }
                }) { Text(stringResource(R.string.Library_Delete)) }
            },
            dismissButton = { TextButton(onClick = { deleting = null }) { Text(stringResource(R.string.Reader_Cancel)) } },
        )
    }
}

private val covers = object : LruCache<Long, ImageBitmap>(8 * 1024 * 1024) {
    override fun sizeOf(key: Long, value: ImageBitmap) = value.width * value.height * 4
}

/** The first page of a saved archive as its cover, decoded at the card's width. */
@Composable
fun ArchiveCover(services: AppServices, item: LibraryItem, modifier: Modifier) {
    BoxWithConstraints(modifier.background(MaterialTheme.colorScheme.surfaceVariant)) {
        val width = with(LocalDensity.current) { maxWidth.roundToPx() }
        val image by produceState(covers.get(item.archive.id), item.archive.id) {
            if (value != null || item.archive.state != ArchiveState.OK) return@produceState
            value = runCatching {
                withContext(Dispatchers.IO) {
                    services.openArchive(item).use { r ->
                        val bytes = r.readEntry(r.pages.first())
                        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
                        var sample = 1
                        while (bounds.outWidth / (sample * 2) >= width) sample *= 2
                        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })!!.asImageBitmap()
                    }
                }
            }.getOrNull()?.also { covers.put(item.archive.id, it) }
        }
        image?.let { Image(it, null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop) }
    }
}
