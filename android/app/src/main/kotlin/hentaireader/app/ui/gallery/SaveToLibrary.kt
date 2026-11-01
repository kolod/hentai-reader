package hentaireader.app.ui.gallery

import android.Manifest
import android.net.Uri
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilledTonalButton
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import hentaireader.app.AppServices
import hentaireader.app.R
import hentaireader.app.downloads.DownloadService
import hentaireader.app.storage.SafDocumentTree
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveState
import hentaireader.host.data.DownloadRow
import hentaireader.host.data.DownloadState
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.LibraryItem
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * Save to library (006 FR-001, 007 FR-028, T060): "Save to library" → the download, its state and
 * "12 / 40" on the button (tap pauses or resumes) → "In library" (opens the saved copy). The first
 * save without a library folder explains and opens the system folder picker at Documents; the
 * notification permission is asked once (Android 13+).
 */
@Composable
fun SaveToLibraryButton(services: AppServices, sourceId: String, gallery: JsonObject, onOpenSaved: (LibraryItem) -> Unit) {
    val context = LocalContext.current
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    val key = remember(gallery) { GalleryKey(sourceId, gallery["id"]!!.jsonPrimitive.content) }
    var refresh by remember { mutableIntStateOf(0) }
    var askFolder by remember { mutableStateOf(false) }

    val saved by produceState<LibraryItem?>(null, key, refresh) {
        value = ArchiveRepository(services.database()).find(key).firstOrNull { it.archive.state == ArchiveState.OK }
    }
    val download by produceState<DownloadRow?>(null, key, refresh) {
        value = services.downloadRepository().activeFor(key)
    }
    LaunchedEffect(key) {
        services.downloads().changes.collect { if (it.key == key) refresh++ }
    }

    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    fun enqueue() = scope.launch {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && !services.prefs.value.notificationAsked) {
            services.updatePrefs { it.copy(notificationAsked = true) }
            permission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        services.downloads().enqueue(sourceId, gallery, services.prefs.value.sourceSettings[sourceId])
        DownloadService.start(context)
        refresh++
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
        if (uri != null) scope.launch {
            services.addLibraryFolder(uri)
            enqueue()
        }
    }

    val d = download
    val label = when {
        saved != null -> stringResource(R.string.Save_InLibrary)
        d == null -> stringResource(R.string.Save_ToLibrary)
        d.state == DownloadState.QUEUED -> stringResource(R.string.Save_Queued)
        d.state == DownloadState.PAUSED -> stringResource(R.string.Save_Paused, strings.number(d.pagesDone), strings.number(d.pageCount))
        d.state == DownloadState.LIMITED && d.errorKind == "LoginRequired" -> stringResource(R.string.Save_LoginRequired)
        d.state == DownloadState.LIMITED -> stringResource(R.string.Save_Limited)
        d.state == DownloadState.FAILED -> stringResource(R.string.Save_Failed)
        else -> stringResource(R.string.Save_Saving, strings.number(d.pagesDone), strings.number(d.pageCount))
    }
    FilledTonalButton(onClick = {
        val s = saved
        when {
            s != null -> onOpenSaved(s)
            d == null -> scope.launch {
                if (FolderRepository(services.database()).list().isEmpty()) askFolder = true else enqueue()
            }
            d.state == DownloadState.DOWNLOADING || d.state == DownloadState.QUEUED -> scope.launch { services.downloads().pause(d.id); refresh++ }
            else -> scope.launch { services.downloads().resume(d.id); DownloadService.start(context); refresh++ }
        }
    }) { Text(label) }

    if (askFolder) AlertDialog(
        onDismissRequest = { askFolder = false },
        title = { Text(stringResource(R.string.Settings_Library)) },
        text = { Text(stringResource(R.string.Library_ChooseFolder_Prompt)) },
        confirmButton = { TextButton(onClick = { askFolder = false; picker.launch(SafDocumentTree.DOCUMENTS) }) { Text(stringResource(R.string.Settings_AddFolder)) } },
        dismissButton = { TextButton(onClick = { askFolder = false }) { Text(stringResource(R.string.Reader_Cancel)) } },
    )
}
