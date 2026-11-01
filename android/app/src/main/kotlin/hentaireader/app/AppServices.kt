package hentaireader.app

import android.app.ActivityManager
import android.content.Context
import android.net.Uri
import android.util.LruCache
import hentaireader.app.downloads.DownloadService
import hentaireader.app.images.ThumbnailCache
import hentaireader.app.storage.AssetScriptStore
import hentaireader.app.storage.SafDocumentTree
import hentaireader.app.system.NetworkStateImpl
import hentaireader.host.Preferences
import hentaireader.host.archive.ArchiveMetadata
import hentaireader.host.archive.CbzReader
import hentaireader.host.archive.MetadataUpdater
import hentaireader.host.data.ArchiveRepository
import hentaireader.host.data.DownloadReason
import hentaireader.host.data.DownloadRepository
import hentaireader.host.data.FolderRepository
import hentaireader.host.data.GalleryKey
import hentaireader.host.data.LibraryDatabase
import hentaireader.host.data.LibraryFolder
import hentaireader.host.data.LibraryItem
import hentaireader.host.data.ProgressRepository
import hentaireader.host.data.ReaderSettingsRepository
import hentaireader.host.downloads.DownloadQueue
import hentaireader.host.downloads.DownloadSource
import java.time.Instant
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import hentaireader.host.engine.CallResult
import hentaireader.host.engine.LoadException
import hentaireader.host.http.CookieJar
import hentaireader.host.http.LiveTransport
import hentaireader.host.reading.ImageBudget
import hentaireader.host.storage.PageCache
import hentaireader.host.sources.SessionOptions
import hentaireader.host.sources.SourceCatalog
import hentaireader.host.sources.SourceSession
import hentaireader.host.storage.SourceStorage
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull

/** A bundled source as the navigation shows it (003 FR-010): available, or unavailable with the reason. */
data class SourceEntry(val id: String, val name: String, val error: String? = null)

/**
 * The single owner of everything the screens share: preferences, bundled sources and their
 * sessions, the database. Nothing that writes to disk or the network exists before the adult
 * confirmation (007 FR-035): sessions, storage and the database are created only after it.
 */
class AppServices(context: Context) {
    private val app = context.applicationContext
    val dataDir: File = app.noBackupFilesDir
    private val prefsFile = File(dataDir, "preferences.json")
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private val _prefs = MutableStateFlow(Preferences.load(prefsFile))
    val prefs: StateFlow<Preferences> = _prefs.asStateFlow()

    private val scripts = AssetScriptStore(app.assets)
    private val contract by lazy { scripts.contractScripts() }
    private val bundles by lazy { SourceCatalog.load(scripts) }
    private val transport by lazy { LiveTransport() }

    private val _sources = MutableStateFlow<List<SourceEntry>?>(null)
    /** null while the bundled sources are being loaded. */
    val sources: StateFlow<List<SourceEntry>?> = _sources.asStateFlow()

    private val sessionLock = Mutex()
    private val sessions = HashMap<String, SourceSession>()
    /** Covers and thumbnails of every screen. */
    val thumbnails = ThumbnailCache()

    /** Galleries just loaded by the gallery screen, handed to the reader without a second request. */
    val galleries = LruCache<String, JsonObject>(8)

    /** Memory for decoded pages, from what the device grants the app (007 FR-016). */
    val imageBudget: ImageBudget = ImageBudget.forMemoryClass((app.getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager).memoryClass)

    private val cacheLock = Mutex()
    private var cache: PageCache? = null

    private val dbLock = Mutex()
    private var db: LibraryDatabase? = null

    /** Screens asked for from outside (the download notification opens Downloads). */
    val openRequests = kotlinx.coroutines.flow.MutableSharedFlow<String>(replay = 1, extraBufferCapacity = 4)

    /** The device network as downloads see it (Wi-Fi only, 007 FR-023). */
    val network by lazy { NetworkStateImpl(app) }

    private val queueLock = Mutex()
    private var queue: DownloadQueue? = null

    /** Changes and saves the preferences; nothing is written before the adult confirmation. */
    suspend fun updatePrefs(change: (Preferences) -> Preferences) {
        val next = change(_prefs.value).sanitised()
        _prefs.value = next
        if (next.adultConfirmed) next.save(prefsFile)
    }

    /** Loads every bundled source once (after the adult confirmation). */
    fun start() {
        if (!_prefs.value.adultConfirmed || _sources.value != null) return
        scope.launch {
            _sources.value = bundles.map { bundle ->
                try {
                    val session = session(bundle.id)
                    SourceEntry(bundle.id, (session.manifest["name"] as? JsonPrimitive)?.contentOrNull ?: bundle.id)
                } catch (e: LoadException) {
                    SourceEntry(bundle.id, bundle.id, e.message ?: "")
                }
            }
            // Downloads that were running continue (007 FR-026).
            val q = downloads()
            q.start()
            if (q.hasWork()) DownloadService.start(app)
            // Archives copied in by other apps or a computer appear (007 FR-029).
            runCatching { scanLibrary() }
        }
    }

    /** The running session of a source, opened on first use; throws [LoadException] when it cannot run. */
    suspend fun session(id: String): SourceSession = sessionLock.withLock {
        check(_prefs.value.adultConfirmed) { "no source may run before the adult confirmation" }
        sessions[id] ?: run {
            val bundle = bundles.first { it.id == id }
            val storage = withContext(Dispatchers.IO) { SourceStorage(File(dataDir, "storage/$id.json")) }
            // Cookies are kept in memory until the encrypted cookie store (US6, T072) persists them.
            val options = SessionOptions(transport, enforcePoliteness = true, storage = storage, scripts = contract, cookies = CookieJar())
            SourceSession.open(bundle.script, "$id.js", options).also { sessions[id] = it }
        }
    }

    /** Calls a source function with the user's settings for that source (005 FR-026). */
    suspend fun call(session: SourceSession, sourceId: String, fn: String, args: List<JsonElement?>): CallResult =
        session.call(fn, args, _prefs.value.sourceSettings[sourceId])

    /** The on-disk page cache (005 FR-023, 007 FR-020), created on first use after the adult confirmation. */
    suspend fun pageCache(): PageCache = cacheLock.withLock {
        check(_prefs.value.adultConfirmed) { "nothing is written before the adult confirmation" }
        cache ?: withContext(Dispatchers.IO) {
            PageCache(File(app.cacheDir, "pages"), _prefs.value.cacheLimitMb * 1024L * 1024)
        }.also { cache = it }
    }

    /** The local database, created on first use (after the adult confirmation). */
    suspend fun database(): LibraryDatabase = dbLock.withLock {
        check(_prefs.value.adultConfirmed) { "nothing is written before the adult confirmation" }
        db ?: LibraryDatabase.open(File(dataDir, "library.db")).also { db = it }
    }

    /** The download queue (007 FR-026), created on first use after the adult confirmation. */
    suspend fun downloads(): DownloadQueue = queueLock.withLock {
        queue ?: run {
            val db = database()
            DownloadQueue(
                DownloadRepository(db), FolderRepository(db), ArchiveRepository(db), ProgressRepository(db), ReaderSettingsRepository(db),
                sources = { id ->
                    runCatching { session(id) }.getOrNull()?.let { s ->
                        DownloadSource(s, (s.manifest["name"] as? JsonPrimitive)?.contentOrNull ?: id, _prefs.value.sourceSettings[id])
                    }
                },
                tree = ::tree,
                stagingRoot = dataDir,
                cache = pageCache(),
                app = "Hentai Reader ${BuildConfig.VERSION_NAME} (Android)",
                blockedReason = { if (_prefs.value.wifiOnlyDownloads && network.status.value.metered) DownloadReason.WAITING_WIFI else null },
            ).also { q ->
                queue = q
                // A network or setting change may let held-back downloads start.
                scope.launch {
                    network.status.map { it.metered }.distinctUntilChanged().drop(1).collect {
                        q.conditionsChanged()
                        if (q.hasWork()) DownloadService.start(app)
                    }
                }
                scope.launch {
                    _prefs.map { it.wifiOnlyDownloads }.distinctUntilChanged().drop(1).collect {
                        q.conditionsChanged()
                        if (q.hasWork()) DownloadService.start(app)
                    }
                }
            }
        }
    }

    suspend fun downloadRepository() = DownloadRepository(database())

    /** History covers (saved copy, page cache, network only when allowed). */
    val historyCovers by lazy { hentaireader.app.images.HistoryCovers(this) }

    /** Rescans every library folder (start, Library opened, pull-to-refresh; 007 FR-029). */
    suspend fun scanLibrary() {
        val db = database()
        hentaireader.host.library.LibraryScanner(FolderRepository(db), ArchiveRepository(db), ::tree).scanAll()
    }

    suspend fun libraryFolders(): hentaireader.host.library.LibraryFolders {
        val db = database()
        return hentaireader.host.library.LibraryFolders(FolderRepository(db), ArchiveRepository(db), DownloadRepository(db), ::tree)
    }

    /** A library folder's document tree. */
    fun tree(folder: LibraryFolder): SafDocumentTree = SafDocumentTree(app, Uri.parse(folder.path))

    /** Adds a folder picked with the system folder picker and keeps access to it (007 FR-028). */
    suspend fun addLibraryFolder(treeUri: Uri): LibraryFolder {
        SafDocumentTree.persist(app, treeUri)
        val display = SafDocumentTree.displayPath(treeUri, app.getString(R.string.Settings_CacheLocation_Internal))
        return FolderRepository(database()).add(treeUri.toString(), display, Instant.now())
    }

    /** Opens a saved gallery's archive for reading (off the main thread). */
    suspend fun openArchive(item: LibraryItem): CbzReader = withContext(Dispatchers.IO) {
        val folder = FolderRepository(database()).list().first { it.id == item.archive.folderId }
        CbzReader(tree(folder).openRead(item.archive.relPath))
    }

    /**
     * Writes progress and reader settings into the saved archives of a gallery (006 FR-009):
     * when leaving the reader, not on every scroll; a failed write leaves the row dirty.
     */
    suspend fun writeBack(key: GalleryKey) = withContext(Dispatchers.IO) {
        val db = database()
        val archives = ArchiveRepository(db)
        val progress = ProgressRepository(db).get(key)
        val reader = ReaderSettingsRepository(db).get(key)
        for (item in archives.find(key).filter { it.archive.format == "hentai-reader-1" }) {
            try {
                val folder = FolderRepository(db).list().first { it.id == item.archive.folderId }
                val tree = tree(folder)
                val file = tree.archive(item.archive.relPath)
                val current = file.openRead().use { CbzReader(it).readMetadata() } ?: continue
                val next = current.copy(
                    progress = progress?.let { ArchiveMetadata.ProgressBlock(it.page, it.offset, it.finished, it.updatedAt) } ?: current.progress,
                    reader = reader?.let { ArchiveMetadata.ReaderBlock(it.zoom, it.fitWidth, it.gaps, it.updatedAt) } ?: current.reader,
                )
                MetadataUpdater.update(file, next.toUtf8())
                tree.stat(item.archive.relPath)?.let { archives.setFileStamp(item.archive.id, it.size, it.modifiedAt) }
                archives.setDirty(item.archive.id, false)
            } catch (e: Exception) {
                archives.setDirty(item.archive.id, true)
            }
        }
    }
}
