package hentaireader.app.storage

import android.content.res.AssetManager
import hentaireader.host.engine.ContractScripts
import hentaireader.host.sources.ScriptStore

/** The bundled scripts packed into the APK by :app:packAssets (Constitution III built-in copy). */
class AssetScriptStore(private val assets: AssetManager) : ScriptStore {
    override fun sourceIds(): List<String> = assets.list("sources")?.toList().orEmpty()

    override fun read(path: String): String = assets.open("sources/$path").bufferedReader().use { it.readText() }

    fun contractScripts() = ContractScripts(
        assets.open("contract/prelude.js").bufferedReader().use { it.readText() },
        assets.open("contract/host-glue.js").bufferedReader().use { it.readText() },
    )
}
