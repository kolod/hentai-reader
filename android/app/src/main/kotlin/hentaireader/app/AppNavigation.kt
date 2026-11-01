package hentaireader.app

import androidx.activity.compose.BackHandler
import androidx.activity.compose.LocalActivity
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.systemGestures
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.TravelExplore
import androidx.compose.material.icons.outlined.CollectionsBookmark
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavDestination.Companion.hasRoute
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.toRoute
import hentaireader.app.system.safeControls
import hentaireader.app.ui.common.HrTopBar
import hentaireader.app.ui.common.LocalOpenMenu
import hentaireader.host.Preferences
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import androidx.lifecycle.createSavedStateHandle
import androidx.lifecycle.viewmodel.compose.viewModel
import hentaireader.app.ui.gallery.GalleryScreen
import hentaireader.app.ui.gallery.GalleryViewModel
import hentaireader.app.ui.reader.ReaderScreen
import hentaireader.app.ui.reader.ReaderViewModel
import hentaireader.app.ui.downloads.DownloadsScreen
import hentaireader.app.ui.gallery.SaveToLibraryButton
import hentaireader.app.ui.library.LibraryScreen
import hentaireader.app.system.Notifications
import hentaireader.host.data.LibraryItem
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.ArchiveState
import hentaireader.app.ui.history.HistoryScreen
import androidx.compose.runtime.LaunchedEffect
import hentaireader.app.ui.search.SearchScreen
import hentaireader.app.ui.search.DefaultSearchStar
import hentaireader.app.ui.settings.SettingsScreen
import hentaireader.app.ui.login.LoginScreen
import hentaireader.app.ui.search.SearchViewModel

// Section roots (one back stack each, as the Windows navigation items) and the screens below them.
@Serializable data class SourceRoute(val sourceId: String)
@Serializable data object HistoryRoute
@Serializable data object LibraryRoute
@Serializable data object DownloadsRoute
@Serializable data object SettingsRoute
@Serializable data class LoginRoute(val sourceId: String)
@Serializable data class TagSearchRoute(val sourceId: String, val query: String)
@Serializable data class GalleryRoute(val sourceId: String, val galleryId: String, val title: String)
/** [page] = page to open at; -1 = continue at the saved position. */
@Serializable data class ReaderRoute(val sourceId: String, val galleryId: String, val title: String, val page: Int, val archiveId: Long = -1)

/**
 * The navigation shell (007 FR-007, FR-008, FR-030; contracts/android-ui.md): the pane is a modal
 * drawer, closed by default on every screen; ☰ opens it, and a left-edge swipe does too unless the
 * system uses that edge for its back gesture. Back closes the drawer first, then goes back within
 * the section, and on a section's first screen closes the app.
 */
@Composable
fun AppNavigation(services: AppServices) {
    val sources by services.sources.collectAsStateWithLifecycle()
    val list = sources
    if (list == null) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        return
    }
    val prefs by services.prefs.collectAsStateWithLifecycle()
    val start: Any = remember(list) { startRoute(prefs, list) }
    val nav = rememberNavController()
    val drawer = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val activity = LocalActivity.current
    val entry by nav.currentBackStackEntryAsState()
    val destination = entry?.destination

    // With gesture navigation the left edge belongs to the system's Back.
    val gestureNav = WindowInsets.systemGestures.getLeft(LocalDensity.current, LocalLayoutDirection.current) > 0
    val atSectionRoot = destination != null && SECTION_ROOTS.any { destination.hasRoute(it) }
    val inReader = destination?.hasRoute(ReaderRoute::class) == true

    ModalNavigationDrawer(
        drawerState = drawer,
        // The reader never shows the pane (005 FR-016, 007 FR-008).
        gesturesEnabled = !inReader && (drawer.isOpen || !gestureNav),
        drawerContent = {
            ModalDrawerSheet(Modifier.safeControls()) {
                Column(Modifier.verticalScroll(rememberScrollState()).padding(vertical = 12.dp)) {
                    Text(stringResource(R.string.Nav_Sources), style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(16.dp, 8.dp))
                    for (source in list) {
                        // A source that cannot run stays listed with the reason (003 FR-010).
                        if (source.error != null) {
                            NavigationDrawerItem(
                                label = { Column { Text(source.name); Text(stringResource(R.string.Source_Unavailable), style = MaterialTheme.typography.labelSmall) } },
                                icon = { Icon(Icons.Filled.TravelExplore, null) },
                                selected = false,
                                onClick = {},
                                modifier = Modifier.padding(horizontal = 12.dp),
                            )
                            continue
                        }
                        NavigationDrawerItem(
                            label = { Text(source.name) },
                            icon = { Icon(Icons.Filled.TravelExplore, null) },
                            selected = destination?.hasRoute(SourceRoute::class) == true && entry?.toRoute<SourceRoute>()?.sourceId == source.id,
                            onClick = {
                                scope.launch { drawer.close() }
                                nav.openSection(SourceRoute(source.id))
                                scope.launch { services.updatePrefs { it.copy(lastSourceId = source.id) } }
                            },
                            modifier = Modifier.padding(horizontal = 12.dp),
                        )
                    }
                    HorizontalDivider(Modifier.padding(vertical = 8.dp))
                    DrawerItem(stringResource(R.string.Nav_History), Icons.Filled.History, destination?.hasRoute(HistoryRoute::class) == true) {
                        scope.launch { drawer.close() }; nav.openSection(HistoryRoute)
                    }
                    DrawerItem(stringResource(R.string.Nav_Library), Icons.Outlined.CollectionsBookmark, destination?.hasRoute(LibraryRoute::class) == true) {
                        scope.launch { drawer.close() }; nav.openSection(LibraryRoute)
                    }
                    DrawerItem(stringResource(R.string.Nav_Downloads), Icons.Filled.Download, destination?.hasRoute(DownloadsRoute::class) == true) {
                        scope.launch { drawer.close() }; nav.openSection(DownloadsRoute)
                    }
                    DrawerItem(stringResource(R.string.Nav_Settings), Icons.Filled.Settings, destination?.hasRoute(SettingsRoute::class) == true) {
                        scope.launch { drawer.close() }; nav.openSection(SettingsRoute)
                    }
                }
            }
        },
    ) {
        CompositionLocalProvider(LocalOpenMenu provides { scope.launch { drawer.open() } }) {
            NavHost(nav, startDestination = start) {
                composable<SourceRoute> { e ->
                    val id = e.toRoute<SourceRoute>().sourceId
                    val vm = viewModel(key = "search:$id") { SearchViewModel(services, id, null, createSavedStateHandle()) }
                    SearchScreen(vm, services.thumbnails, onOpenGallery = { nav.openGallery(id, it) }, onBack = null,
                        star = { DefaultSearchStar(vm, prefs.defaultQueries) })
                }
                composable<TagSearchRoute> { e ->
                    val r = e.toRoute<TagSearchRoute>()
                    val vm = viewModel { SearchViewModel(services, r.sourceId, r.query, createSavedStateHandle()) }
                    SearchScreen(vm, services.thumbnails, onOpenGallery = { nav.openGallery(r.sourceId, it) }, onBack = { nav.popBackStack() })
                }
                composable<GalleryRoute> { e ->
                    val r = e.toRoute<GalleryRoute>()
                    val vm = viewModel { GalleryViewModel(services, r.sourceId, r.galleryId) }
                    GalleryScreen(vm, services.thumbnails, r.title, onBack = { nav.popBackStack() },
                        onSearchTag = { q -> nav.navigate(TagSearchRoute(r.sourceId, q)) },
                        onRead = { page -> nav.navigate(ReaderRoute(r.sourceId, r.galleryId, vm.gallery?.let { (it["title"] as? JsonPrimitive)?.contentOrNull } ?: r.title, page)) },
                        actions = { g -> SaveToLibraryButton(services, r.sourceId, g) { item -> nav.openSaved(item) } })
                }
                composable<ReaderRoute> { e ->
                    val r = e.toRoute<ReaderRoute>()
                    val vm = viewModel { ReaderViewModel(services, r.sourceId, r.galleryId, r.title, r.page, r.archiveId, createSavedStateHandle()) }
                    ReaderScreen(vm, keepScreenOn = prefs.keepScreenOn, onBack = { nav.popBackStack() })
                }
                composable<HistoryRoute> {
                    HistoryScreen(services, services.historyCovers) { entry ->
                        // The saved copy when there is one, otherwise online at the saved position (006 FR-019).
                        scope.launch {
                            val saved = ArchiveRepository(services.database()).find(entry.key).firstOrNull { it.archive.state == ArchiveState.OK }
                            if (saved != null) nav.openSaved(saved) else nav.navigate(ReaderRoute(entry.key.sourceId, entry.key.galleryId, entry.title, -1))
                        }
                    }
                }
                composable<LibraryRoute> { LibraryScreen(services) { item -> nav.openSaved(item) } }
                composable<DownloadsRoute> { DownloadsScreen(services) }
                composable<SettingsRoute> { SettingsScreen(services, onSignIn = { id -> nav.navigate(LoginRoute(id)) }) }
                composable<LoginRoute> { e -> LoginScreen(services, e.toRoute<LoginRoute>().sourceId, onDone = { nav.popBackStack() }) }
            }
        }
    }

    // The download notification opens Downloads.
    LaunchedEffect(nav) {
        services.openRequests.collect {
            services.openRequests.resetReplayCache()
            if (it == Notifications.OPEN_DOWNLOADS) nav.openSection(DownloadsRoute)
        }
    }

    // Registered after the NavHost, so these win over its own back handling.
    BackHandler(enabled = atSectionRoot && !drawer.isOpen) { activity?.finish() }
    BackHandler(enabled = drawer.isOpen) { scope.launch { drawer.close() } }
}

private val SECTION_ROOTS = listOf(SourceRoute::class, HistoryRoute::class, LibraryRoute::class, DownloadsRoute::class, SettingsRoute::class)

/** Opens a saved gallery offline in the reader, at its saved position. */
fun NavHostController.openSaved(item: LibraryItem) {
    navigate(ReaderRoute(item.archive.key.sourceId, item.archive.key.galleryId, item.archive.title, -1, item.archive.id))
}

/** Opens a gallery from a result card. */
fun NavHostController.openGallery(sourceId: String, item: JsonObject) {
    val id = (item["id"] as? JsonPrimitive)?.contentOrNull ?: return
    navigate(GalleryRoute(sourceId, id, (item["title"] as? JsonPrimitive)?.contentOrNull ?: ""))
}

/** Switches to a section, keeping each section's own back stack (state saved and restored). */
fun NavHostController.openSection(route: Any) {
    navigate(route) {
        popUpTo(graph.findStartDestination().id) { saveState = true }
        launchSingleTop = true
        restoreState = true
    }
}

/** The start page setting (006 FR-021); an unavailable source falls back to the first available one. */
private fun startRoute(prefs: Preferences, sources: List<SourceEntry>): Any {
    val available = sources.filter { it.error == null }
    fun source(id: String?) = available.firstOrNull { it.id == id }?.let { SourceRoute(it.id) }
    val fallback: Any = available.firstOrNull()?.let { SourceRoute(it.id) } ?: SettingsRoute
    return when {
        prefs.startPage == Preferences.START_HISTORY -> HistoryRoute
        prefs.startPage == Preferences.START_LIBRARY -> LibraryRoute
        prefs.startPage.startsWith(Preferences.START_SOURCE_PREFIX) -> source(prefs.startPage.removePrefix(Preferences.START_SOURCE_PREFIX)) ?: fallback
        else -> source(prefs.lastSourceId) ?: fallback
    }
}

@Composable
private fun DrawerItem(label: String, icon: androidx.compose.ui.graphics.vector.ImageVector, selected: Boolean, onClick: () -> Unit) {
    NavigationDrawerItem(
        label = { Text(label) },
        icon = { Icon(icon, null) },
        selected = selected,
        onClick = onClick,
        modifier = Modifier.padding(horizontal = 12.dp),
    )
}
