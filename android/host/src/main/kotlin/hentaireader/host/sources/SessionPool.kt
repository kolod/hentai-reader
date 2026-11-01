package hentaireader.host.sources

import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Keeps source engines loaded only while they are used (007 T077, resource-budgets.md): an engine
 * idle for [idleMillis] is closed, and all idle engines are closed on demand (low memory). The
 * session itself stays, so its holders never notice: the next call starts a fresh engine with the
 * same storage, cookies and settings. One timer per session, started when its last call ends;
 * nothing runs while no source is used.
 */
class SessionPool(private val scope: CoroutineScope, private val idleMillis: Long = 120_000) {
    private val sessions = ConcurrentHashMap.newKeySet<SourceSession>()
    private val timers = ConcurrentHashMap<SourceSession, Job>()

    fun add(session: SourceSession) {
        sessions.add(session)
        session.onIdle = { schedule(session) }
        schedule(session)
    }

    /** Closes every engine that is not running a call now. */
    suspend fun closeIdle() {
        for (s in sessions) {
            timers.remove(s)?.cancel()
            s.closeIdleEngine()
        }
    }

    private fun schedule(session: SourceSession) {
        timers.remove(session)?.cancel()
        val job = scope.launch {
            delay(idleMillis)
            session.closeIdleEngine()
        }
        timers[session] = job
        job.invokeOnCompletion { timers.remove(session, job) }
    }
}
