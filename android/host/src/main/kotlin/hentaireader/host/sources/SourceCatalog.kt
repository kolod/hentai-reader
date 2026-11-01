package hentaireader.host.sources

/** A bundled source script (Constitution III: the app's built-in copy). */
class SourceBundle(val id: String, private val load: () -> String) {
    val script: String by lazy(load)
}

/**
 * Where bundled scripts come from: the APK's assets on a device, the repository's `sources/` in
 * tests. Laid out as `<id>/<id>.js`.
 */
interface ScriptStore {
    fun sourceIds(): List<String>
    fun read(path: String): String
}

object SourceCatalog {
    fun load(store: ScriptStore): List<SourceBundle> =
        store.sourceIds().sorted().map { id -> SourceBundle(id) { store.read("$id/$id.js") } }
}
