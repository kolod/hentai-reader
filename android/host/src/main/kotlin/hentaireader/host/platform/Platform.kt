package hentaireader.host.platform

import java.io.InputStream
import java.io.OutputStream
import java.time.Instant
import kotlinx.coroutines.flow.StateFlow

/**
 * Random access to one file: an archive in a library folder (on Android a document opened
 * through its file descriptor). [write] is available only on writable sources.
 */
interface SeekableSource : AutoCloseable {
    val size: Long

    /** Reads up to [length] bytes at [position] into [buffer]; returns the count (-1 at the end). */
    fun read(position: Long, buffer: ByteArray, offset: Int = 0, length: Int = buffer.size - offset): Int

    /** Writes at [position] (append = [size]); throws when the source is read-only. */
    fun write(position: Long, buffer: ByteArray, offset: Int = 0, length: Int = buffer.size - offset)

    /** Cuts the file to [size] bytes (used to undo a failed append). */
    fun truncate(size: Long)
}

/** A file or folder below a library folder, as the platform lists it. */
data class DocumentEntry(
    /** Path relative to the tree root, with `/` separators. */
    val relPath: String,
    val name: String,
    val isDirectory: Boolean,
    val size: Long,
    val modifiedAt: Instant,
    val mimeType: String?,
)

/**
 * A library folder (on Android a document tree chosen with the system folder picker). All
 * operations are blocking and must be called off the UI thread.
 */
interface DocumentTree {
    /** True when the folder exists and the app still may access it. */
    fun isAvailable(): Boolean

    /** Every file below the root (recursively), one platform query per folder. */
    fun listFiles(): List<DocumentEntry>

    /** One file's entry, or null when it does not exist. */
    fun stat(relPath: String): DocumentEntry?

    /** One file as an archive (read, append, replace). */
    fun archive(relPath: String): ArchiveFile

    fun openRead(relPath: String): SeekableSource
    fun openWrite(relPath: String): SeekableSource

    /** Creates an empty file in the root (an existing name gets a platform-chosen variant); returns its relative path. */
    fun create(name: String, mimeType: String): String
    fun rename(relPath: String, newName: String): String
    fun delete(relPath: String): Boolean
    fun inputStream(relPath: String): InputStream
    fun outputStream(relPath: String): OutputStream
}

/** Wall-clock time, replaceable in tests. */
fun interface Clock {
    fun now(): Instant

    companion object {
        val SYSTEM = Clock { Instant.now() }
    }
}

/** The device's network as downloads see it (007 FR-023). */
interface NetworkState {
    data class Status(val connected: Boolean, val metered: Boolean)

    val status: StateFlow<Status>
}
