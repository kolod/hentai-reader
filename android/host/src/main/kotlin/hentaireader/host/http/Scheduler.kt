package hentaireader.host.http

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

data class Politeness(val minIntervalMs: Int, val maxConcurrency: Int)

/**
 * Enforces a minimum interval between request starts and a maximum number of requests in flight,
 * first come first served (port of the runner's and the Windows Scheduler). Shared by script and
 * image requests.
 *
 * @param limits current limits, or null to disable (offline replay).
 */
class Scheduler(private val limits: () -> Politeness?) {
    private val order = Mutex()
    private val lock = Any()
    private var inFlight = 0
    private var lastStart = Long.MIN_VALUE / 2
    private var released = CompletableDeferred<Unit>()

    suspend fun acquire() {
        order.withLock {
            val lim = limits()
            if (lim != null) {
                while (true) {
                    val wait = synchronized(lock) { if (inFlight < lim.maxConcurrency) null else released } ?: break
                    wait.await()
                }
                while (true) {
                    val remaining = lastStart + lim.minIntervalMs - nowMs()
                    if (remaining <= 0) break
                    delay(remaining)
                }
            }
            synchronized(lock) {
                lastStart = nowMs()
                inFlight++
            }
        }
    }

    fun release() {
        val signal = synchronized(lock) {
            inFlight--
            released.also { released = CompletableDeferred() }
        }
        signal.complete(Unit)
    }

    private fun nowMs() = System.nanoTime() / 1_000_000
}
