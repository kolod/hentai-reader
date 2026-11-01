package hentaireader.app.ui.search

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import hentaireader.app.R
import hentaireader.app.images.ThumbnailCache
import hentaireader.app.images.SourceImage
import hentaireader.app.ui.common.ErrorPanel
import hentaireader.app.ui.common.LocalOpenMenu
import hentaireader.app.ui.common.Strings
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.sources.SourceSession
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull

/**
 * A source's search (003 US2, 007 US2; contracts/android-ui.md "Search screen"): free text in the
 * source's syntax, exactly the declared filters in a bottom sheet, an endless grid of result cards.
 */
@Composable
fun SearchScreen(
    vm: SearchViewModel,
    thumbnails: ThumbnailCache,
    onOpenGallery: (JsonObject) -> Unit,
    onBack: (() -> Unit)?,
    star: @Composable () -> Unit = {},
) {
    val strings = rememberStrings()
    val focus = LocalFocusManager.current
    var showFilters by rememberSaveable { mutableStateOf(false) }
    val grid = rememberLazyGridState()

    // Endless results: the next page loads as the grid nears its end (003 FR-012).
    LaunchedEffect(vm, grid) {
        snapshotFlow { grid.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0 }
            .collect { last -> if (last >= vm.items.size - 6) vm.loadMore() }
    }

    Scaffold(
        topBar = {
            SearchBar(
                query = vm.query,
                onQueryChange = { vm.query = it },
                onSearch = { focus.clearFocus(); vm.search() },
                onBack = onBack,
                onFilters = { showFilters = true },
                hasFilters = vm.filters.isNotEmpty(),
                star = star,
            )
        },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        val loadError = vm.loadError
        if (loadError != null) {
            Text(stringResource(R.string.Source_Unavailable) + "\n" + loadError, Modifier.padding(padding).padding(24.dp))
            return@Scaffold
        }
        LazyVerticalGrid(
            state = grid,
            columns = GridCells.Adaptive(160.dp),
            modifier = Modifier.padding(padding).fillMaxSize(),
            contentPadding = PaddingValues(12.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            vm.total?.let { total ->
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Text(strings.plural("Search_ResultCount", total), style = MaterialTheme.typography.labelLarge)
                }
            }
            itemsIndexed(vm.items, key = { i, item -> "$i:${item["id"]}" }) { _, item ->
                ResultCard(item, strings, thumbnails, vm.session) { onOpenGallery(item) }
            }
            item(span = { GridItemSpan(maxLineSpan) }) {
                Footer(vm)
            }
        }
    }

    if (showFilters) FilterSheet(vm, strings) { changed ->
        showFilters = false
        if (changed) vm.search()
    }
}

@Composable
private fun Footer(vm: SearchViewModel) {
    val error = vm.error
    Box(Modifier.fillMaxWidth().padding(16.dp).navigationBarsPadding(), contentAlignment = Alignment.Center) {
        when {
            error != null -> ErrorPanel(error, onRetry = vm::retry)
            vm.status == SearchStatus.LOADING || vm.session == null -> CircularProgressIndicator()
            vm.status == SearchStatus.END && vm.items.isEmpty() -> Text(stringResource(R.string.Search_Empty))
            vm.status == SearchStatus.END -> Text(stringResource(R.string.Search_End), style = MaterialTheme.typography.labelMedium)
        }
    }
}

@Composable
private fun SearchBar(
    query: String,
    onQueryChange: (String) -> Unit,
    onSearch: () -> Unit,
    onBack: (() -> Unit)?,
    onFilters: () -> Unit,
    hasFilters: Boolean,
    star: @Composable () -> Unit,
) {
    val openMenu = LocalOpenMenu.current
    Surface(color = MaterialTheme.colorScheme.surface) {
        Row(
            Modifier.fillMaxWidth()
                .windowInsetsPadding(WindowInsets.safeDrawing.union(WindowInsets.displayCutout).only(WindowInsetsSides.Top + WindowInsetsSides.Horizontal))
                .padding(horizontal = 4.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (onBack != null) {
                IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.Gallery_Back)) }
            } else {
                IconButton(onClick = openMenu) { Icon(Icons.Filled.Menu, stringResource(R.string.Nav_OpenMenu)) }
            }
            star()
            TextField(
                value = query,
                onValueChange = onQueryChange,
                modifier = Modifier.weight(1f).clip(RoundedCornerShape(28.dp)),
                placeholder = { Text(stringResource(R.string.Search_Placeholder), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search, autoCorrectEnabled = false),
                keyboardActions = KeyboardActions(onSearch = { onSearch() }),
                colors = TextFieldDefaults.colors(focusedIndicatorColor = Color.Transparent, unfocusedIndicatorColor = Color.Transparent),
            )
            if (hasFilters) IconButton(onClick = onFilters) { Icon(Icons.Filled.FilterList, stringResource(R.string.Search_Filters)) }
        }
    }
}

/** A result card: cover, title, language, type, page count, key tags (003 US2). */

@Composable
fun ResultCard(item: JsonObject, strings: Strings, thumbnails: ThumbnailCache, session: SourceSession?, onClick: () -> Unit) {
    Card(Modifier.fillMaxWidth().clickable(onClick = onClick)) {
        SourceImage(thumbnails, session, item["cover"] as? JsonObject, null, Modifier.fillMaxWidth().aspectRatio(0.7f))
        Column(Modifier.padding(8.dp)) {
            Text(item.str("title") ?: "", style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
            val meta = listOfNotNull(
                item.str("language")?.let(strings::languageName),
                item.str("type")?.let(strings::typeLabel),
                (item["pageCount"] as? JsonPrimitive)?.doubleOrNull?.let { strings.plural("Gallery_PageCount", it.toLong()) },
            ).joinToString(" · ")
            Text(meta, style = MaterialTheme.typography.labelSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
            val tags = (item["tags"] as? JsonArray).orEmpty().take(4).mapNotNull { (it as? JsonObject)?.str("name") }
            if (tags.isNotEmpty()) {
                Text(tags.joinToString(", "), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

/**
 * Exactly the filters the source declares, labels from the contract vocabulary (003 FR-011):
 * single and multi as chips (languages alphabetical by their localized name), number and range as
 * number fields. Closing without a change does not search again.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun FilterSheet(vm: SearchViewModel, strings: Strings, onClose: (changed: Boolean) -> Unit) {
    val before = remember { vm.currentFilters() }
    val close = { onClose(vm.currentFilters() != before) }
    ModalBottomSheet(onDismissRequest = close) {
        Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 20.dp).navigationBarsPadding(), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            for (f in vm.filters) {
                Text(strings.filterLabel(f.id), style = MaterialTheme.typography.titleSmall)
                fun label(v: String) = when (f.id) {
                    "sort" -> strings.sortLabel(v)
                    "type" -> strings.typeLabel(v)
                    "language" -> strings.languageName(v)
                    else -> v
                }
                val values = if (f.id == "language") f.values.sortedBy { label(it).lowercase(strings.locale) } else f.values
                when (f.kind) {
                    "single" -> FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        if (f.default == null) FilterChip(f.single == null, { f.single = null }, { Text(stringResource(R.string.Search_Any)) })
                        for (v in values) FilterChip(f.single == v, { f.single = v }, { Text(label(v)) })
                    }
                    "multi" -> FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (v in values) FilterChip(v in f.multi, { if (v in f.multi) f.multi.remove(v) else f.multi.add(v) }, { Text(label(v)) })
                    }
                    "number" -> NumberField(f.number, "${strings.number(f.lower)}–${strings.number(f.upper)}", Modifier.fillMaxWidth()) {
                        f.number = it?.coerceIn(f.lower, f.upper)
                    }
                    "range" -> Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        NumberField(f.min, stringResource(R.string.Search_From), Modifier.weight(1f)) { f.min = it?.coerceIn(f.lower, f.upper) }
                        NumberField(f.max, stringResource(R.string.Search_To), Modifier.weight(1f)) { f.max = it?.coerceIn(f.lower, f.upper) }
                    }
                }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End)) {
                OutlinedButton(onClick = vm::resetFilters) { Text(stringResource(R.string.Search_ResetFilters)) }
                Button(onClick = close) { Text(stringResource(R.string.Search_Button)) }
            }
            Spacer(Modifier.height(8.dp))
        }
    }
}

@Composable
private fun NumberField(value: Double?, label: String, modifier: Modifier, onChange: (Double?) -> Unit) {
    var text by remember(value) { mutableStateOf(value?.let { if (it % 1.0 == 0.0) it.toLong().toString() else it.toString() } ?: "") }
    OutlinedTextField(
        value = text,
        onValueChange = { t ->
            text = t
            onChange(t.replace(',', '.').toDoubleOrNull())
        },
        label = { Text(label) },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
        modifier = modifier,
    )
}

private fun JsonObject.str(key: String) = (this[key] as? JsonPrimitive)?.contentOrNull

