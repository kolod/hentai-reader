package hentaireader.host.storage

import hentaireader.host.platform.Clock
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.time.Duration
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.LongArraySerializer
import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json

/**
 * Disk cache of downloaded page images (port of the Windows PageCache; 005 data-model "Page
 * cache"): files named by the SHA-256 of their key, an index (name → size, last use) that can
 * always be rebuilt from the folder, least recently used files evicted beyond the limit. The cache
 * can move to another folder while in use (006 US7): during a move each file is read from wherever
 * it is, new files go to the new folder, and [moveStep] moves the rest.
 *
 * Android addition (007 FR-020): before a write, if free space on the cache volume is below
 * min(1 GiB, 10 % of the volume), the page is not cached and [onReserve] is called (once per
 * cache instance); reading continues from the network.
 */
class PageCache(
    folder: File,
    limitBytes: Long,
    private val clock: Clock = Clock.SYSTEM,
    moveFrom: File? = null,
    private val freeSpace: (File) -> Pair<Long, Long> = { it.usableSpace to it.totalSpace },
) : AutoCloseable {
    private class Entry(var size: Long, var lastUsed: Instant, var inOld: Boolean = false)

    private val lock = Any()
    private var folder: File = folder
    private var old: File? = moveFrom?.takeIf { it.isDirectory && !same(it, folder) }
    private var limit = limitBytes
    private var size = 0L
    private var dirty = false
    private var lastFlush: Instant = clock.now()
    private val entries: MutableMap<String, Entry>
    private var reserveNoticed = false

    /** Called once when a page was not cached because storage is nearly full. */
    var onReserve: (() -> Unit)? = null

    init {
        folder.mkdirs()
        entries = loadIndex() ?: rebuild()
        size = entries.values.sumOf { it.size }
        synchronized(lock) { evict() }
    }

    val sizeBytes: Long get() = synchronized(lock) { size }

    var limitBytes: Long
        get() = synchronized(lock) { limit }
        set(value) = synchronized(lock) {
            limit = value
            evict()
        }

    /** Current folder (new files go here). */
    val currentFolder: File get() = synchronized(lock) { folder }

    /** Folder still being moved away from, or null. */
    val movingFrom: File? get() = synchronized(lock) { old }

    /** Files still to move, and all files. */
    val moveProgress: Pair<Int, Int> get() = synchronized(lock) { entries.values.count { it.inOld } to entries.size }

    fun get(key: String): ByteArray? {
        val name = fileName(key)
        synchronized(lock) {
            val entry = entries[name] ?: return null
            val bytes = try {
                pathOf(name, entry).readBytes()
            } catch (_: IOException) {
                forget(name, entry)
                return null
            }
            entry.lastUsed = clock.now()
            touch()
            return bytes
        }
    }

    fun contains(key: String): Boolean = synchronized(lock) { entries.containsKey(fileName(key)) }

    /** Stores a page; returns false when it was skipped because storage is nearly full. */
    suspend fun put(key: String, bytes: ByteArray): Boolean = withContext(Dispatchers.IO) {
        val target = synchronized(lock) { folder }
        if (belowReserve(target)) {
            val notify = synchronized(lock) { !reserveNoticed.also { reserveNoticed = true } }
            if (notify) onReserve?.invoke()
            return@withContext false
        }
        val name = fileName(key)
        val path = File(target, name)
        val temp = File(target, "$name.${UUID.randomUUID().toString().replace("-", "")}.tmp")
        temp.writeBytes(bytes)
        synchronized(lock) {
            if (!temp.renameTo(path)) {
                path.delete()
                temp.renameTo(path)
            }
            entries[name]?.let { previous ->
                size -= previous.size
                if (previous.inOld) tryDelete(pathOf(name, previous))
            }
            entries[name] = Entry(bytes.size.toLong(), clock.now())
            size += bytes.size
            evict()
            touch()
        }
        true
    }

    /** Deletes every cached image and the index; nothing else in the data folder. */
    fun clear() = synchronized(lock) {
        folder.listFiles()?.forEach { tryDelete(it) }
        old?.listFiles()?.forEach { tryDelete(it) }
        entries.clear()
        size = 0
        dirty = false
    }

    fun flush() = synchronized(lock) {
        if (!dirty) return
        val data = entries.mapValues { (_, e) -> longArrayOf(e.size, e.lastUsed.toEpochMilli(), if (e.inOld) 1 else 0) }
        try {
            val temp = File(folder, "$INDEX.tmp")
            temp.writeText(Json.encodeToString(INDEX_SERIALIZER, data))
            File(folder, INDEX).delete()
            if (!temp.renameTo(File(folder, INDEX))) return
        } catch (_: IOException) {
            // Folder gone or full: the index is rebuilt from the files next time.
            return
        }
        dirty = false
        lastFlush = clock.now()
    }

    override fun close() = flush()

    /**
     * Starts moving the cache to [target]: new files go there at once, existing ones are moved by
     * [moveStep]. Calling it with the previous folder during a move turns the move around.
     */
    fun beginMove(target: File) = synchronized(lock) {
        if (same(target, folder)) return
        target.mkdirs()
        val reverse = old != null && same(target, old!!)
        check(old == null || reverse) { "a cache move is already running" }
        old = folder
        folder = target
        for (e in entries.values) e.inOld = !reverse || !e.inOld
        dirty = true
        flush()
    }

    /**
     * Moves up to [count] files to the current folder; returns false when nothing is left (the
     * move is then complete and the old folder cleaned up). An I/O failure (no space, folder gone)
     * is thrown and loses nothing: unmoved files stay readable where they are.
     */
    suspend fun moveStep(count: Int = 20): Boolean = withContext(Dispatchers.IO) {
        repeat(count) {
            ensureActive()
            val (name, from, to) = synchronized(lock) {
                val next = entries.entries.firstOrNull { it.value.inOld }
                if (next == null) {
                    complete()
                    return@withContext false
                }
                Triple(next.key, File(old!!, next.key), File(folder, next.key))
            }
            if (from.exists()) {
                if (!from.renameTo(to)) {
                    // Another volume: copy, then delete.
                    from.copyTo(to, overwrite = true)
                    from.delete()
                }
            }
            synchronized(lock) {
                val entry = entries[name]
                if (entry != null && entry.inOld) {
                    if (to.exists()) entry.inOld = false else forget(name, entry)
                }
                touch()
            }
        }
        synchronized(lock) {
            if (entries.values.any { it.inOld }) return@withContext true
            complete()
            false
        }
    }

    private fun complete() {
        val o = old ?: return
        tryDelete(File(o, INDEX))
        if (o.isDirectory && o.list().isNullOrEmpty()) o.delete()
        old = null
        dirty = true
        flush()
    }

    private fun belowReserve(dir: File): Boolean {
        val (free, total) = freeSpace(dir)
        if (total <= 0) return false
        return free < reserveBytes(total)
    }

    private fun pathOf(name: String, entry: Entry) = File(if (entry.inOld && old != null) old!! else folder, name)

    private fun touch() {
        dirty = true
        if (Duration.between(lastFlush, clock.now()) >= FLUSH_INTERVAL) flush()
    }

    private fun evict() {
        if (size <= limit) return
        for ((name, entry) in entries.entries.sortedBy { it.value.lastUsed }.map { it.key to it.value }) {
            if (size <= limit) break
            tryDelete(pathOf(name, entry))
            forget(name, entry)
        }
    }

    private fun forget(name: String, entry: Entry) {
        entries.remove(name)
        size -= entry.size
        dirty = true
    }

    private fun loadIndex(): MutableMap<String, Entry>? = try {
        val file = File(folder, INDEX)
        if (!file.exists()) null else {
            val data = Json.decodeFromString(INDEX_SERIALIZER, file.readText())
            val result = HashMap<String, Entry>()
            for ((name, v) in data) {
                // Files deleted behind our back are dropped; the index never lists missing files.
                if (v.size < 2) continue
                val entry = Entry(v[0], Instant.ofEpochMilli(v[1]), v.size > 2 && v[2] == 1L && old != null)
                if (pathOf(name, entry).exists()) result[name] = entry
                else if (entry.inOld && File(folder, name).exists()) result[name] = Entry(entry.size, entry.lastUsed)
            }
            result
        }
    } catch (_: Exception) {
        null
    }

    private fun rebuild(): MutableMap<String, Entry> {
        val result = HashMap<String, Entry>()
        old?.listFiles()?.forEach { f ->
            if (!f.name.endsWith(".tmp") && !f.name.startsWith(INDEX)) result[f.name] = Entry(f.length(), Instant.ofEpochMilli(f.lastModified()), inOld = true)
        }
        folder.listFiles()?.forEach { f ->
            if (f.name.endsWith(".tmp") || f.name.startsWith(INDEX)) tryDelete(f)
            else result[f.name] = Entry(f.length(), Instant.ofEpochMilli(f.lastModified()))
        }
        dirty = true
        return result
    }

    companion object {
        private const val INDEX = "index.json"
        private const val GIB = 1024L * 1024 * 1024
        private val FLUSH_INTERVAL: Duration = Duration.ofSeconds(10)
        private val INDEX_SERIALIZER = MapSerializer(String.serializer(), LongArraySerializer())

        /** Free space kept on a volume (007 FR-020): min(1 GiB, 10 % of the volume). */
        fun reserveBytes(totalBytes: Long): Long = minOf(GIB, totalBytes / 10)

        /** Cache key of a page; the fingerprint separates different setting values (e.g. image quality). */
        fun key(sourceId: String, galleryId: String, page: Int, fingerprint: String) = "$sourceId|$galleryId|$page|$fingerprint"

        /** Stable fingerprint of a source's setting values (order-independent), as on Windows. */
        fun fingerprint(settings: Map<String, String>?): String {
            val text = settings.orEmpty().entries.sortedBy { it.key }.joinToString("\n") { "${it.key}=${it.value}" }
            return sha256(text).take(16)
        }

        private fun fileName(key: String) = sha256(key)

        private fun sha256(text: String) = MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }

        private fun same(a: File, b: File) = a.absoluteFile.normalize().path.equals(b.absoluteFile.normalize().path, ignoreCase = true)

        private fun tryDelete(f: File) {
            f.delete()
        }
    }
}
