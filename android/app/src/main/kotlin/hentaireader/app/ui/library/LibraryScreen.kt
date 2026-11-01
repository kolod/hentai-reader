package hentaireader.app.ui.library

import android.graphics.BitmapFactory
import android.net.Uri
import android.util.LruCache
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
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
import hentaireader.app.storage.SafDocumentTree
import hentaireader.app.ui.common.HrTopBar
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveState
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.LibraryFolder
import hentaireader.host.data.LibraryItem
import hentaireader.host.data.LibraryQuery
import hentaireader.host.data.LibrarySort
import hentaireader.host.data.ReadFilter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * The library (006 US1/US3/US4, 007 T067/T068): every saved gallery of every folder with cover,
 * title, source, pages, progress and folder; search by title while typing, source and read-state
 * filters, three sorts; a pull rescans the folders (archives copied in by other apps appear);
 * galleries of an unavailable folder are greyed, with "Grant access" and "Remove folder". A long
 * press deletes an archive from the device after a confirmation (there is no recycle bin).
 */
@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3Api::class)
@Composable
fun LibraryScreen(services: AppServices, onOpen: (LibraryItem) -> Unit) {
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    var refresh by remember { mutableIntStateOf(0) }
    var scanning by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf<LibraryItem?>(null) }
    var text by rememberSaveable { mutableStateOf("") }
    var source by rememberSaveable { mutableStateOf<String?>(null) }
    var read by rememberSaveable { mutableStateOf(ReadFilter.ALL) }
    var sort by rememberSaveable { mutableStateOf(LibrarySort.ADDED) }
    var sortMenu by remember { mutableStateOf(false) }
    var regrant by remember { mutableStateOf<LibraryFolder?>(null) }

    suspend fun rescan() {
        scanning = true
        runCatching { services.scanLibrary() }
        scanning = false
        refresh++
    }
    LaunchedEffect(Unit) { rescan() }
    LaunchedEffect(Unit) { services.downloads().changes.collect { refresh++ } }

    val items by produceState<List<LibraryItem>?>(null, refresh, text, source, read, sort) {
        delay(150)   // results follow the typing without a query per keystroke
        value = ArchiveRepository(services.database()).list(LibraryQuery(text = text, sourceId = source, read = read, sort = sort, limit = 5000))
    }
    val sources by produceState(emptyList<String>(), refresh) {
        value = ArchiveRepository(services.database()).list(LibraryQuery(limit = 5000)).map { it.archive.key.sourceId }.distinct().sorted()
    }
    val unavailable by produceState(emptyList<Pair<LibraryFolder, String>>(), refresh) {
        value = withContext(Dispatchers.IO) {
            FolderRepository(services.database()).list().mapNotNull { f -> services.tree(f).unavailableReason()?.let { f to it } }
        }
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
        if (uri != null) scope.launch {
            services.addLibraryFolder(uri)
            rescan()
        }
        regrant = null
    }

    Scaffold(
        topBar = {
            HrTopBar(stringResource(R.string.Nav_Library)) {
                Box {
                    IconButton(onClick = { sortMenu = true }) { Icon(Icons.AutoMirrored.Filled.Sort, stringResource(R.string.Library_SortAdded)) }
                    DropdownMenu(sortMenu, onDismissRequest = { sortMenu = false }) {
                        for ((s, label) in listOf(LibrarySort.ADDED to R.string.Library_SortAdded, LibrarySort.LAST_READ to R.string.Library_SortLastRead, LibrarySort.TITLE to R.string.Library_SortTitle)) {
                            DropdownMenuItem(text = { Text((if (s == sort) "✓ " else "") + stringResource(label)) }, onClick = { sort = s; sortMenu = false })
                        }
                    }
                }
            }
        },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        PullToRefreshBox(isRefreshing = scanning, onRefresh = { scope.launch { rescan() } }, modifier = Modifier.padding(padding).fillMaxSize()) {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(150.dp),
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        for ((folder, reason) in unavailable) {
                            Surface(color = MaterialTheme.colorScheme.errorContainer, shape = MaterialTheme.shapes.medium) {
                                Column(Modifier.padding(12.dp).fillMaxWidth()) {
                                    Text(folder.displayPath, style = MaterialTheme.typography.titleSmall)
                                    Text(stringResource(if (reason == SafDocumentTree.NO_ACCESS) R.string.Library_NoAccess else R.string.Library_FolderMissing), style = MaterialTheme.typography.bodySmall)
                                    Row {
                                        if (reason == SafDocumentTree.NO_ACCESS) TextButton(onClick = { regrant = folder; picker.launch(Uri.parse(folder.path)) }) {
                                            Text(stringResource(R.string.Library_GrantAccess))
                                        }
                                        TextButton(onClick = { scope.launch { runCatching { services.libraryFolders().removeKeepFiles(folder.id) }; refresh++ } }) {
                                            Text(stringResource(R.string.Settings_RemoveFolder))
                                        }
                                    }
                                }
                            }
                        }
                        OutlinedTextField(text, { text = it }, Modifier.fillMaxWidth(), placeholder = { Text(stringResource(R.string.Library_Search)) }, singleLine = true)
                        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            FilterChip(source == null, { source = null }, { Text(stringResource(R.string.Library_FilterAllSources)) })
                            for (s in sources) FilterChip(source == s, { source = s }, { Text(if (s == "local") stringResource(R.string.Library_LocalSource) else s) })
                        }
                        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            for ((r, label) in listOf(ReadFilter.ALL to R.string.Library_FilterAll, ReadFilter.UNREAD to R.string.Library_Unread,
                                ReadFilter.IN_PROGRESS to R.string.Library_InProgress, ReadFilter.FINISHED to R.string.Gallery_Finished)) {
                                FilterChip(read == r, { read = r }, { Text(stringResource(label)) })
                            }
                        }
                    }
                }
                val list = items.orEmpty()
                if (items != null && list.isEmpty()) item(span = { GridItemSpan(maxLineSpan) }) {
                    Text(stringResource(if (text.isBlank() && source == null && read == ReadFilter.ALL) R.string.Library_Empty else R.string.Library_NoMatches), Modifier.padding(16.dp))
                }
                items(list, key = { it.archive.id }) { item ->
                    val available = item.archive.state == ArchiveState.OK
                    Card(Modifier.fillMaxWidth().alpha(if (available) 1f else 0.5f).combinedClickable(
                        onClick = { if (available) onOpen(item) },
                        onLongClick = { deleting = item },
                    )) {
                        ArchiveCover(services, item, Modifier.fillMaxWidth().aspectRatio(0.7f))
                        Column(Modifier.padding(8.dp)) {
                            Text(item.archive.title, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            val progress = item.progress
                            val meta = listOfNotNull(
                                if (item.archive.key.sourceId == "local") stringResource(R.string.Library_LocalSource) else item.archive.key.sourceId,
                                strings.plural("Gallery_PageCount", item.archive.pages.size.toLong()),
                                progress?.let { if (it.finished) stringResource(R.string.Gallery_Finished) else stringResource(R.string.Gallery_Progress, strings.number(it.page + 1), strings.number(it.pageCount)) },
                            ).joinToString(" · ")
                            Text(meta, style = MaterialTheme.typography.labelSmall, maxLines = 2)
                            Text(item.folderDisplay(), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
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

/** The folder's last path segment ("Hentai Reader"), from its tree URI. */
private fun LibraryItem.folderDisplay(): String = Uri.decode(folderPath).substringAfterLast(':').substringAfterLast('/')

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
