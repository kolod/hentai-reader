package hentaireader.host.engine

import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CompletableDeferred

/**
 * A dedicated thread that owns one QuickJS runtime. Every engine access is a work item on this
 * thread; completions of Kotlin work (HTTP, storage, sleep) post their settlement here too.
 * QuickJS records the stack top of the creating thread, so the runtime is created on it as well.
 */
internal class EngineThread(name: String) {
    private val queue = LinkedBlockingQueue<Runnable>()
    @Volatile private var closed = false
    private val thread = Thread(null, ::run, name, STACK_BYTES).apply {
        isDaemon = true
        start()
    }

    val isCurrent: Boolean get() = Thread.currentThread() === thread

    fun post(action: Runnable) {
        if (!closed) queue.add(action)
    }

    suspend fun <T> invoke(block: () -> T): T {
        val result = CompletableDeferred<T>()
        post {
            try {
                result.complete(block())
            } catch (e: Throwable) {
                result.completeExceptionally(e)
            }
        }
        return result.await()
    }

    /**
     * Runs queued work on the engine thread until [done] returns true or the deadline passes
     * (then [EngineTimeoutException]). Must be called on the engine thread.
     */
    fun pumpUntil(deadlineNanos: Long, done: () -> Boolean) {
        while (!done()) {
            val wait = deadlineNanos - System.nanoTime()
            if (wait <= 0) throw EngineTimeoutException()
            queue.poll(minOf(wait, POLL_NANOS), TimeUnit.NANOSECONDS)?.run()
        }
    }

    // Blocks while there is no work: an idle engine never wakes up (007 FR-019).
    private fun run() {
        while (!closed) {
            val action = queue.take()
            try {
                action.run()
            } catch (_: Throwable) {
                // A late completion of a call that already ended (timed out, engine replaced) has
                // nobody waiting for it; it must not take the whole app down.
            }
        }
    }

    fun close() {
        closed = true
        queue.add(Runnable {})   // wakes the thread so it can end
    }

    private companion object {
        const val STACK_BYTES = 8L * 1024 * 1024
        val POLL_NANOS = TimeUnit.MILLISECONDS.toNanos(250)
    }
}

internal class EngineTimeoutException : RuntimeException("call exceeded the time limit")
