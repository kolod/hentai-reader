package hentaireader.app

import android.content.Context
import hentaireader.app.storage.AssetScriptStore
import hentaireader.host.Preferences
import hentaireader.host.data.LibraryDatabase
import hentaireader.host.engine.LoadException
import hentaireader.host.http.CookieJar
import hentaireader.host.http.LiveTransport
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
    private val dbLock = Mutex()
    private var db: LibraryDatabase? = null

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

    /** The local database, created on first use (after the adult confirmation). */
    suspend fun database(): LibraryDatabase = dbLock.withLock {
        check(_prefs.value.adultConfirmed) { "nothing is written before the adult confirmation" }
        db ?: LibraryDatabase.open(File(dataDir, "library.db")).also { db = it }
    }
}
