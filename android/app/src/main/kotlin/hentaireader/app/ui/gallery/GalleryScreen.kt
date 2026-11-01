package hentaireader.app.ui.gallery

import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.viewModelScope
import hentaireader.app.AppServices
import hentaireader.app.R
import hentaireader.app.images.SourceImage
import hentaireader.app.images.ThumbnailCache
import hentaireader.app.ui.common.ErrorInfo
import hentaireader.app.ui.common.ErrorPanel
import hentaireader.app.ui.common.HrTopBar
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.Progress
import hentaireader.host.data.ProgressRepository
import hentaireader.host.engine.LoadException
import hentaireader.host.sources.SourceSession
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull

/** A gallery from `getGallery` (003 US3). */
class GalleryViewModel(private val services: AppServices, val sourceId: String, val galleryId: String) : ViewModel() {
    var session by mutableStateOf<SourceSession?>(null)
        private set
    var gallery by mutableStateOf<JsonObject?>(null)
        private set
    var error by mutableStateOf<ErrorInfo?>(null)
        private set
    var progress by mutableStateOf<Progress?>(null)
        private set

    private val sourceName get() = services.sources.value?.firstOrNull { it.id == sourceId }?.name ?: sourceId

    init {
        load()
    }

    fun load() {
        error = null
        viewModelScope.launch {
            val s = try {
                services.session(sourceId)
            } catch (e: LoadException) {
                error = ErrorInfo("ScriptError", null, e.message ?: "")
                return@launch
            }
            session = s
            val result = services.call(s, sourceId, "getGallery", listOf(JsonPrimitive(galleryId)))
            if (result.ok) {
                gallery = result.value as JsonObject
                services.galleries.put("$sourceId|$galleryId", result.value as JsonObject)
            } else {
                error = ErrorInfo.of(result.error!!, sourceName)
            }
        }
    }

    /** Saved progress (005 FR-019); refreshed when the screen comes back from the reader. */
    fun refreshProgress() {
        viewModelScope.launch { progress = ProgressRepository(services.database()).get(GalleryKey(sourceId, galleryId)) }
    }

    /** The user's explicit retry also clears a rate limit (003 FR-013). */
    fun retry() {
        session?.clearLimit()
        load()
    }
}

/**
 * Gallery details (003 US3, contracts/android-ui.md "Gallery screen"): metadata, tags grouped by
 * namespace (a tap searches for the tag in the same source), one thumbnail per page with sprite
 * cells cropped. [onRead] receives the page to start at (a thumbnail tap opens the reader there).
 */

@Composable
fun GalleryScreen(
    vm: GalleryViewModel,
    thumbnails: ThumbnailCache,
    title: String,
    onBack: () -> Unit,
    onSearchTag: (String) -> Unit,
    onRead: ((Int) -> Unit)? = null,
    actions: @Composable (JsonObject) -> Unit = {},
) {
    val strings = rememberStrings()
    val context = LocalContext.current
    val g = vm.gallery
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { vm.refreshProgress() }
    Scaffold(
        topBar = {
            HrTopBar(g?.str("title") ?: title, onBack = onBack) {
                g?.str("webUrl")?.let { url ->
                    IconButton(onClick = { context.startActivity(Intent(Intent.ACTION_VIEW, url.toUri())) }) {
                        Icon(Icons.AutoMirrored.Filled.OpenInNew, stringResource(R.string.Gallery_OpenInBrowser))
                    }
                }
            }
        },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        val error = vm.error
        when {
            error != null -> ErrorPanel(error, vm::retry, Modifier.padding(padding))
            g == null -> Box(Modifier.padding(padding).fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            else -> LazyVerticalGrid(
                columns = GridCells.Adaptive(96.dp),
                modifier = Modifier.padding(padding).fillMaxSize(),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            SourceImage(thumbnails, vm.session, g["cover"] as? JsonObject, null, Modifier.width(120.dp).aspectRatio(0.7f))
                            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                Text(g.str("title") ?: "", style = MaterialTheme.typography.titleMedium)
                                g.str("titleOriginal")?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                                Meta(stringResource(R.string.Gallery_Language), g.str("language")?.let(strings::languageName))
                                Meta(stringResource(R.string.Gallery_Type), g.str("type")?.let(strings::typeLabel))
                                Meta(stringResource(R.string.Gallery_Pages), g.num("pageCount")?.let { strings.number(it.toLong()) })
                                Meta(stringResource(R.string.Gallery_Uploaded), g.str("uploadedAt")?.let(strings::date))
                                Meta(stringResource(R.string.Gallery_Rating), g.num("rating")?.let { strings.decimal(it) })
                                Meta(stringResource(R.string.Gallery_Uploader), g.str("uploader"))
                            }
                        }
                        val progress = vm.progress
                        if (progress != null) {
                            Text(
                                if (progress.finished) stringResource(R.string.Gallery_Finished)
                                else stringResource(R.string.Gallery_Progress, strings.number(progress.page + 1), strings.number(progress.pageCount)),
                                style = MaterialTheme.typography.labelLarge,
                            )
                        }
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            if (onRead != null) {
                                if (progress != null && !progress.finished) {
                                    Button(onClick = { onRead(-1) }) { Text(stringResource(R.string.Gallery_Continue)) }
                                    OutlinedButton(onClick = { onRead(0) }) { Text(stringResource(R.string.Gallery_ReadFromStart)) }
                                } else {
                                    Button(onClick = { onRead(0) }) { Text(stringResource(R.string.Gallery_Read)) }
                                }
                            }
                            actions(g)
                        }
                        // Tags grouped by namespace, in the order the source gives them.
                        val groups = (g["tags"] as? JsonArray).orEmpty().mapNotNull { it as? JsonObject }.groupBy { it.str("namespace") }
                        for ((ns, tags) in groups) {
                            Column {
                                Text(strings.namespaceLabel(ns), style = MaterialTheme.typography.labelLarge)
                                FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                    for (tag in tags) {
                                        val name = tag.str("name") ?: continue
                                        AssistChip(onClick = { onSearchTag(tagQuery(ns, name)) }, label = { Text(name) })
                                    }
                                }
                            }
                        }
                    }
                }
                val pages = (g["pages"] as? JsonArray).orEmpty().mapNotNull { it as? JsonObject }
                items(pages, key = { it.num("index")?.toInt() ?: 0 }) { page ->
                    val index = page.num("index")?.toInt() ?: 0
                    val w = page.num("width") ?: 2.0
                    val h = page.num("height") ?: 3.0
                    Column(Modifier.clickable(enabled = onRead != null) { onRead?.invoke(index) }) {
                        SourceImage(thumbnails, vm.session, page["thumbnail"] as? JsonObject, null,
                            Modifier.fillMaxWidth().aspectRatio((w / h).toFloat().coerceIn(0.3f, 3f)),
                            contentScale = androidx.compose.ui.layout.ContentScale.Fit)
                        Text(strings.number(index + 1), style = MaterialTheme.typography.labelSmall, modifier = Modifier.align(Alignment.CenterHorizontally))
                    }
                }
                item(span = { GridItemSpan(maxLineSpan) }) { Box(Modifier.navigationBarsPadding()) }
            }
        }
    }
}

@Composable
private fun Meta(label: String, value: String?) {
    if (value.isNullOrEmpty()) return
    Text("$label: $value", style = MaterialTheme.typography.bodySmall)
}

/** Tag search query with the contract's `namespace:name_with_underscores` convention (contract 1.1.1). */
fun tagQuery(ns: String?, name: String): String {
    val term = name.replace(' ', '_')
    return if (ns.isNullOrEmpty()) term else "$ns:$term"
}

private fun JsonObject.str(key: String) = (this[key] as? JsonPrimitive)?.contentOrNull
private fun JsonObject.num(key: String) = (this[key] as? JsonPrimitive)?.doubleOrNull

