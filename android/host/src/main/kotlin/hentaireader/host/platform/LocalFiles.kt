package hentaireader.host.platform

import java.io.File
import java.io.OutputStream
import java.io.RandomAccessFile

/** A local file as a [SeekableSource] (staging, page cache, tests; app-private storage on Android). */
class FileSeekableSource(file: File, writable: Boolean) : SeekableSource {
    private val raf = RandomAccessFile(file, if (writable) "rw" else "r")

    override val size: Long get() = raf.length()

    override fun read(position: Long, buffer: ByteArray, offset: Int, length: Int): Int = synchronized(raf) {
        raf.seek(position)
        raf.read(buffer, offset, length)
    }

    override fun write(position: Long, buffer: ByteArray, offset: Int, length: Int) = synchronized(raf) {
        raf.seek(position)
        raf.write(buffer, offset, length)
    }

    override fun truncate(size: Long) = synchronized(raf) { raf.setLength(size) }

    override fun close() = raf.close()
}

/**
 * One saved archive wherever it lives: read, write in place (metadata appends) and replace as a
 * whole (compaction). [replace] writes a complete new file and swaps it in.
 */
interface ArchiveFile {
    val name: String
    fun openRead(): SeekableSource
    fun openWrite(): SeekableSource
    fun replace(write: (OutputStream) -> Unit)
}

/** An archive on the local file system. */
class LocalArchiveFile(val file: File) : ArchiveFile {
    override val name: String get() = file.name
    override fun openRead(): SeekableSource = FileSeekableSource(file, writable = false)
    override fun openWrite(): SeekableSource = FileSeekableSource(file, writable = true)

    override fun replace(write: (OutputStream) -> Unit) {
        val temp = File(file.path + ".rewrite")
        try {
            temp.outputStream().buffered().use(write)
            if (!temp.renameTo(file)) {
                file.delete()
                check(temp.renameTo(file)) { "cannot replace ${file.name}" }
            }
        } finally {
            temp.delete()
        }
    }
}

/** A library folder on the local file system (tests and the desktop; Android uses document trees). */
class LocalDocumentTree(private val root: File) : DocumentTree {
    override fun isAvailable() = root.isDirectory

    override fun listFiles(): List<DocumentEntry> =
        root.walkTopDown().filter { it.isFile }.map { entry(it) }.toList()

    override fun stat(relPath: String): DocumentEntry? = File(root, relPath).takeIf { it.exists() }?.let(::entry)
    override fun archive(relPath: String): ArchiveFile = LocalArchiveFile(File(root, relPath))
    override fun openRead(relPath: String): SeekableSource = FileSeekableSource(File(root, relPath), writable = false)
    override fun openWrite(relPath: String): SeekableSource = FileSeekableSource(File(root, relPath), writable = true)

    override fun create(name: String, mimeType: String): String {
        root.mkdirs()
        var file = File(root, name)
        var n = 2
        while (file.exists()) file = File(root, "${name.substringBeforeLast('.')} ($n).${name.substringAfterLast('.')}").also { n++ }
        file.createNewFile()
        return file.relativeTo(root).invariantSeparatorsPath
    }

    override fun rename(relPath: String, newName: String): String {
        val from = File(root, relPath)
        val to = File(from.parentFile, newName)
        check(from.renameTo(to)) { "cannot rename $relPath" }
        return to.relativeTo(root).invariantSeparatorsPath
    }

    override fun delete(relPath: String) = File(root, relPath).delete()
    override fun inputStream(relPath: String) = File(root, relPath).inputStream()
    override fun outputStream(relPath: String) = File(root, relPath).outputStream()

    private fun entry(f: File) = DocumentEntry(f.relativeTo(root).invariantSeparatorsPath, f.name, f.isDirectory, f.length(),
        java.time.Instant.ofEpochMilli(f.lastModified()), null)
}
