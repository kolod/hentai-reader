package hentaireader.app.ui.settings

import android.app.Activity
import android.net.Uri
import androidx.activity.compose.LocalActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.selection.selectable
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import hentaireader.app.AppServices
import hentaireader.app.R
import hentaireader.app.storage.SafDocumentTree
import hentaireader.app.system.AppLocale
import hentaireader.app.ui.common.HrTopBar
import hentaireader.app.ui.common.rememberStrings
import hentaireader.host.Preferences
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.LibraryFolder
import hentaireader.host.reading.LoginCookies
import kotlin.math.roundToInt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * Settings (005 FR-026 – FR-033, 006 FR-015 – FR-024, 007 T071; contracts/android-ui.md "Settings"):
 * language, start page, default searches, reader, downloads, cache (limit, Clear, location), library
 * folders, and per source its declared settings and, where it has one, the account (sign in on the
 * site's page or with pasted cookies, status and image limit, sign out).
 */
@Composable
fun SettingsScreen(services: AppServices, onSignIn: (String) -> Unit) {
    val prefs by services.prefs.collectAsStateWithLifecycle()
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    val activity = LocalActivity.current
    var refresh by remember { mutableIntStateOf(0) }
    fun update(change: (Preferences) -> Preferences) = scope.launch { services.updatePrefs(change) }
    val sources = services.sources.collectAsStateWithLifecycle().value.orEmpty().filter { it.error == null }

    Scaffold(topBar = { HrTopBar(stringResource(R.string.Settings_Title)) }, contentWindowInsets = WindowInsets(0)) { padding ->
        LazyColumn(Modifier.padding(padding).fillMaxSize(), contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            item { Section(stringResource(R.string.Settings_Language)) }
            item {
                Column {
                    Choice(stringResource(R.string.Settings_LanguageSystem), prefs.language.isEmpty()) { setLanguage(services, activity, "") }
                    for ((tag, name) in AppLocale.choices()) Choice(name, prefs.language == tag) { setLanguage(services, activity, tag) }
                }
            }

            item { Section(stringResource(R.string.Settings_StartPage)) }
            item {
                Column {
                    Choice(stringResource(R.string.Settings_StartLastSource), prefs.startPage == Preferences.START_LAST_SOURCE) { update { it.copy(startPage = Preferences.START_LAST_SOURCE) } }
                    for (s in sources) {
                        val value = Preferences.START_SOURCE_PREFIX + s.id
                        Choice(s.name, prefs.startPage == value) { update { it.copy(startPage = value) } }
                    }
                    Choice(stringResource(R.string.Nav_History), prefs.startPage == Preferences.START_HISTORY) { update { it.copy(startPage = Preferences.START_HISTORY) } }
                    Choice(stringResource(R.string.Nav_Library), prefs.startPage == Preferences.START_LIBRARY) { update { it.copy(startPage = Preferences.START_LIBRARY) } }
                }
            }
            if (prefs.defaultQueries.isNotEmpty()) {
                for ((id, q) in prefs.defaultQueries) item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(sources.firstOrNull { it.id == id }?.name.orEmpty() + ": " +
                            stringResource(R.string.Settings_DefaultSearch, q.text.ifBlank { stringResource(R.string.Settings_DefaultSearchEmpty) }), Modifier.weight(1f))
                        TextButton(onClick = { update { it.copy(defaultQueries = it.defaultQueries - id) } }) { Text(stringResource(R.string.Settings_RemoveDefaultSearch)) }
                    }
                }
            }

            item { Section(stringResource(R.string.Settings_Reader)) }
            item {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(stringResource(R.string.Settings_Preload) + ": " + strings.number(prefs.preloadPages))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (n in 3..5) Choice(strings.number(n), prefs.preloadPages == n, fill = false) { update { it.copy(preloadPages = n) } }
                    }
                    Toggle(stringResource(R.string.Settings_FitWidth), prefs.fitWidth) { v -> update { it.copy(fitWidth = v) } }
                    SliderRow(stringResource(R.string.Settings_ReadingWidth) + ": " + stringResource(R.string.Settings_ReadingWidthValue, strings.number(prefs.readingWidth)),
                        prefs.readingWidth.toFloat(), 600f..2000f, 13) { v -> update { it.copy(readingWidth = v.roundToInt() / 50 * 50) } }
                    SliderRow(stringResource(R.string.Settings_PageGap) + ": " + if (prefs.pageGap == 0) stringResource(R.string.Settings_PageGapOff) else strings.number(prefs.pageGap),
                        prefs.pageGap.toFloat(), 0f..48f, 11) { v -> update { it.copy(pageGap = v.roundToInt() / 4 * 4) } }
                    Toggle(stringResource(R.string.Settings_KeepScreenOn), prefs.keepScreenOn, stringResource(R.string.Settings_KeepScreenOn_Hint)) { v -> update { it.copy(keepScreenOn = v) } }
                    Toggle(stringResource(R.string.Settings_NetworkCovers), prefs.networkCovers, stringResource(R.string.Settings_NetworkCoversHint)) { v -> update { it.copy(networkCovers = v) } }
                }
            }

            item { Section(stringResource(R.string.Settings_Downloads)) }
            item { Toggle(stringResource(R.string.Settings_WifiOnly), prefs.wifiOnlyDownloads, stringResource(R.string.Settings_WifiOnly_Hint)) { v -> update { it.copy(wifiOnlyDownloads = v) } } }

            item { CacheSection(services) }
            item { FoldersSection(services, refresh) { refresh++ } }

            item { Section(stringResource(R.string.Settings_Sources)) }
            for (s in sources) item { SourceSection(services, s.id, s.name, onSignIn) }
        }
    }
}

private fun setLanguage(services: AppServices, activity: Activity?, tag: String) {
    services.scope.launch {
        services.updatePrefs { it.copy(language = tag) }
        activity?.runOnUiThread {
            AppLocale.publish(activity, tag)
            activity.recreate()
        }
    }
}

@Composable
private fun CacheSection(services: AppServices) {
    val prefs by services.prefs.collectAsStateWithLifecycle()
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    var refresh by remember { mutableIntStateOf(0) }
    val size by produceState(0L, refresh) { value = services.pageCache().sizeBytes }
    val move by services.cacheMove.collectAsStateWithLifecycle()
    val cards = remember { services.cardRoots().keys.toList() }
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Section(stringResource(R.string.Settings_CacheLimit))
        Text(stringResource(R.string.Settings_CacheSize, strings.size(size)))
        // The limit is in MiB; shown with the number the user picked (the system formats in SI units).
        SliderRow(strings.size(prefs.cacheLimitMb * 1_000_000L), prefs.cacheLimitMb.toFloat(), 128f..10240f, 0) { v ->
            val mb = (v.roundToInt() / 128 * 128).coerceIn(128, 10240)
            scope.launch {
                services.updatePrefs { it.copy(cacheLimitMb = mb) }
                services.pageCache().limitBytes = mb * 1024L * 1024
                refresh++
            }
        }
        OutlinedButton(onClick = { scope.launch { services.pageCache().let { c -> withContext(Dispatchers.IO) { c.clear() } }; refresh++ } }) { Text(stringResource(R.string.Settings_ClearCache)) }
        if (cards.isNotEmpty()) {
            Text(stringResource(R.string.Settings_CacheLocation), style = MaterialTheme.typography.titleSmall)
            Choice(stringResource(R.string.Settings_CacheLocation_Internal), prefs.cacheFolder == null, enabled = move == null) { scope.launch { services.moveCache(null) } }
            for (card in cards) Choice(stringResource(R.string.Settings_CacheLocation_Card), prefs.cacheFolder == card, enabled = move == null) { scope.launch { services.moveCache(card) } }
        }
        move?.let { (remaining, total) ->
            Text(stringResource(R.string.Settings_CacheMoving, strings.number(total - remaining), strings.number(total)))
            LinearProgressIndicator(progress = { if (total == 0) 1f else (total - remaining).toFloat() / total }, modifier = Modifier.fillMaxWidth())
        }
    }
}

@Composable
private fun FoldersSection(services: AppServices, refresh: Int, changed: () -> Unit) {
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    val folders by produceState(emptyList<LibraryFolder>(), refresh) { value = FolderRepository(services.database()).list() }
    var removing by remember { mutableStateOf<Pair<LibraryFolder, Int>?>(null) }
    var progress by remember { mutableStateOf<String?>(null) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
        if (uri != null) scope.launch {
            services.addLibraryFolder(uri)
            services.scanLibrary()
            changed()
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Section(stringResource(R.string.Settings_Library))
        for (f in folders) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                RadioButton(selected = f.isTarget, onClick = { scope.launch { FolderRepository(services.database()).setTarget(f.id); changed() } })
                Column(Modifier.weight(1f)) {
                    Text(f.displayPath)
                    if (f.isTarget) Text(stringResource(R.string.Settings_TargetFolder), style = MaterialTheme.typography.labelSmall)
                }
                TextButton(onClick = { scope.launch { removing = f to ArchiveRepository(services.database()).count(f.id) } }) { Text(stringResource(R.string.Settings_RemoveFolder)) }
            }
        }
        progress?.let { Text(it) }
        OutlinedButton(onClick = { picker.launch(SafDocumentTree.DOCUMENTS) }) { Text(stringResource(R.string.Settings_AddFolder)) }
    }
    removing?.let { (folder, count) ->
        val other = folders.firstOrNull { it.id != folder.id }
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text(stringResource(R.string.Settings_RemoveFolder)) },
            text = { Text(stringResource(R.string.Settings_RemoveFolderBody, folder.displayPath, strings.plural("Settings_FolderGalleries", count.toLong()))) },
            confirmButton = {
                if (count > 0 && other != null) TextButton(onClick = {
                    removing = null
                    scope.launch {
                        runCatching { services.libraryFolders().moveAndRemove(folder.id, other.id) { done, total -> progress = strings.get("Settings_MoveProgress", strings.number(done), strings.number(total)) } }
                        progress = null
                        changed()
                    }
                }) { Text(stringResource(R.string.Settings_MoveGalleries)) }
            },
            dismissButton = {
                TextButton(onClick = {
                    removing = null
                    scope.launch { runCatching { services.libraryFolders().removeKeepFiles(folder.id) }; changed() }
                }) { Text(stringResource(R.string.Settings_KeepFiles)) }
            },
        )
    }
}

/** One source: its declared settings and, if it reports account status, the account (005 FR-026 – FR-031). */
@Composable
private fun SourceSection(services: AppServices, sourceId: String, name: String, onSignIn: (String) -> Unit) {
    val prefs by services.prefs.collectAsStateWithLifecycle()
    val strings = rememberStrings()
    val scope = rememberCoroutineScope()
    var refresh by remember { mutableIntStateOf(0) }
    var paste by remember { mutableStateOf(false) }
    var notice by remember { mutableStateOf<String?>(null) }
    val session by produceState<hentaireader.host.sources.SourceSession?>(null) { value = runCatching { services.session(sourceId) }.getOrNull() }
    val s = session ?: return
    val manifest = s.manifest
    val hasAccount = ((manifest["capabilities"] as? JsonObject)?.get("accountStatus") as? JsonPrimitive)?.booleanOrNull == true
    val status by produceState<JsonObject?>(null, refresh, prefs.sourceSettings[sourceId]) {
        value = if (hasAccount) services.call(s, sourceId, "getAccountStatus", emptyList()).value as? JsonObject else null
    }
    val signedIn = (status?.get("loggedIn") as? JsonPrimitive)?.booleanOrNull == true

    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        HorizontalDivider()
        Text(name, style = MaterialTheme.typography.titleMedium)
        for (def in (manifest["settings"] as? JsonArray).orEmpty().map { it.jsonObject }) {
            val id = def["id"]!!.jsonPrimitive.content
            val current = prefs.sourceSettings[sourceId]?.get(id) ?: def["default"]!!.jsonPrimitive.content
            Text(strings.settingLabel(id), style = MaterialTheme.typography.titleSmall)
            for (value in (def["values"] as JsonArray).map { it.jsonPrimitive.content }) {
                Choice(strings.settingValueLabel(id, value), current == value) {
                    // ExHentai needs an account with access; requests known to fail are not sent (005 FR-031).
                    if (id == "site" && value == "exhentai" && (!signedIn || (status?.get("exhentai") as? JsonPrimitive)?.booleanOrNull == false)) {
                        notice = strings.get("ExHentai_NoAccess")
                    } else scope.launch {
                        services.updatePrefs { p -> p.copy(sourceSettings = p.sourceSettings + (sourceId to (p.sourceSettings[sourceId].orEmpty() + (id to value)))) }
                    }
                }
            }
        }
        notice?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        if (hasAccount) {
            Text(if (status == null) stringResource(R.string.Account_Checking) else if (signedIn) stringResource(R.string.Account_SignedIn) else stringResource(R.string.Account_SignedOut))
            (status?.get("imageLimit") as? JsonObject)?.let { limit ->
                val used = (limit["used"] as? JsonPrimitive)?.doubleOrNull?.toLong() ?: 0
                val max = (limit["max"] as? JsonPrimitive)?.doubleOrNull?.toLong() ?: 0
                Text(stringResource(R.string.Account_ImageLimit, strings.number(used), strings.number(max)))
                (limit["resetsAt"] as? JsonPrimitive)?.contentOrNull?.let { Text(stringResource(R.string.Account_LimitResets, strings.date(it))) }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (!signedIn && manifest["loginPage"] != null) Button(onClick = { onSignIn(sourceId) }) { Text(stringResource(R.string.Account_SignIn)) }
                if (!signedIn) OutlinedButton(onClick = { paste = true }) { Text(stringResource(R.string.Account_PasteCookies)) }
                if (signedIn) OutlinedButton(onClick = { s.http.cookies.clear(); refresh++ }) { Text(stringResource(R.string.Account_SignOut)) }
                TextButton(onClick = { refresh++ }) { Text(stringResource(R.string.Account_Refresh)) }
            }
        }
    }

    if (paste) {
        var text by remember { mutableStateOf("") }
        var rejected by remember { mutableStateOf(false) }
        val domain = (manifest["domains"] as? JsonArray)?.firstOrNull()?.jsonPrimitive?.content?.removePrefix("*.") ?: ""
        AlertDialog(
            onDismissRequest = { paste = false },
            title = { Text(stringResource(R.string.Paste_Title)) },
            text = {
                Column {
                    Text(stringResource(R.string.Paste_Hint_Android, domain), style = MaterialTheme.typography.bodySmall)
                    OutlinedTextField(text, { text = it; rejected = false }, Modifier.fillMaxWidth(), minLines = 3)
                    if (rejected) Text(stringResource(R.string.Paste_Rejected), color = MaterialTheme.colorScheme.error)
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    scope.launch {
                        // Validated by asking the account status (005 US6 #3); nothing goes to other domains.
                        val cookies = LoginCookies.select(manifest, LoginCookies.parse(text, domain))
                        s.http.cookies.add(cookies)
                        val ok = (services.call(s, sourceId, "getAccountStatus", emptyList()).value as? JsonObject)?.get("loggedIn")?.let { (it as? JsonPrimitive)?.booleanOrNull } == true
                        if (ok) { paste = false; refresh++ } else { s.http.cookies.clear(); rejected = true }
                    }
                }) { Text(stringResource(R.string.Paste_Save)) }
            },
            dismissButton = { TextButton(onClick = { paste = false }) { Text(stringResource(R.string.Reader_Cancel)) } },
        )
    }
}

@Composable
private fun Section(title: String) {
    Text(title, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(top = 12.dp))
}

@Composable
private fun Choice(label: String, selected: Boolean, fill: Boolean = true, enabled: Boolean = true, onClick: () -> Unit) {
    Row((if (fill) Modifier.fillMaxWidth() else Modifier).selectable(selected, enabled = enabled, onClick = onClick).padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        RadioButton(selected = selected, onClick = null, enabled = enabled)
        Text(label, Modifier.padding(start = 8.dp))
    }
}

@Composable
private fun Toggle(label: String, checked: Boolean, hint: String? = null, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().clickable { onChange(!checked) }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(label)
            hint?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

/** A slider that applies its value when released (no write per drag step). */
@Composable
private fun SliderRow(label: String, value: Float, range: ClosedFloatingPointRange<Float>, steps: Int, onDone: (Float) -> Unit) {
    var current by remember(value) { mutableFloatStateOf(value) }
    Column {
        Text(label)
        Slider(value = current, onValueChange = { current = it }, onValueChangeFinished = { onDone(current) }, valueRange = range, steps = steps)
    }
}
