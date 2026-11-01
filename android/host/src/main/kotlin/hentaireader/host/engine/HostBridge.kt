package hentaireader.host.engine

/** Result of the bridge's http member: an envelope plus the body for "bytes" responses. */
class HttpBridgeResult(val env: String, val bytes: ByteArray?)

/**
 * Host side of the private bridge consumed by contract/host-glue.js
 * (specs/003-windows-host/contracts/host-bridge.md). Every member returns a JSON envelope.
 */
interface HostBridge {
    suspend fun http(metaJson: String, body: ByteArray?): HttpBridgeResult
    fun htmlSelect(markup: String, elementId: Int, selector: String): String
    fun htmlCall(op: String, elementId: Int, arg: String): String
    suspend fun storageGet(key: String): String
    suspend fun storageSet(key: String, valueJson: String): String
    suspend fun sleep(ms: Double): String
    fun log(level: String, text: String)

    /** Called when no source call is in flight, so per-call handles can be dropped. */
    fun idle()

    /** A failure that must abort the call whatever the script does (e.g. unmatched fixture). */
    val fatal: Throwable?
}
