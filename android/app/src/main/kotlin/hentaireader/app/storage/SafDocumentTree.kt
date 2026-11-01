package hentaireader.app.storage

import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import android.provider.DocumentsContract.Document
import hentaireader.host.platform.ArchiveFile
import hentaireader.host.platform.DocumentEntry
import hentaireader.host.platform.DocumentTree
import hentaireader.host.platform.SeekableSource
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.FileNotFoundException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.channels.FileChannel
import java.time.Instant

/**
 * A library folder chosen with the system folder picker (007 FR-028, research R6): listing through
 * one DocumentsContract query per folder, random access through file descriptors, create, rename
 * and delete through the provider. Relative paths are document IDs. Blocking: call off the main
 * thread.
 */
class SafDocumentTree(context: Context, val treeUri: Uri) : DocumentTree {
    private val resolver: ContentResolver = context.contentResolver
    private val rootId = DocumentsContract.getTreeDocumentId(treeUri)

    /** Why the folder cannot be used now, or null: `no_access` (permission gone) or `missing`. */
    fun unavailableReason(): String? {
        val granted = resolver.persistedUriPermissions.any { it.uri == treeUri && it.isReadPermission && it.isWritePermission }
        if (!granted) return NO_ACCESS
        return if (runCatching { query(DocumentsContract.buildDocumentUriUsingTree(treeUri, rootId)).isNotEmpty() }.getOrDefault(false)) null else MISSING
    }

    override fun isAvailable() = unavailableReason() == null

    override fun listFiles(): List<DocumentEntry> {
        val out = ArrayList<DocumentEntry>()
        val pending = ArrayDeque(listOf(rootId))
        while (pending.isNotEmpty()) {
            val parent = pending.removeFirst()
            for (e in query(DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parent))) {
                if (e.isDirectory) pending.add(e.relPath) else out.add(e)
            }
        }
        return out
    }

    override fun stat(relPath: String): DocumentEntry? = runCatching { query(uri(relPath)).firstOrNull() }.getOrNull()

    override fun archive(relPath: String): ArchiveFile = SafArchiveFile(this, relPath)

    override fun openRead(relPath: String): SeekableSource = FdSeekableSource(resolver.openFileDescriptor(uri(relPath), "r") ?: throw FileNotFoundException(relPath), false)

    override fun openWrite(relPath: String): SeekableSource = FdSeekableSource(resolver.openFileDescriptor(uri(relPath), "rw") ?: throw FileNotFoundException(relPath), true)

    override fun create(name: String, mimeType: String): String {
        val created = DocumentsContract.createDocument(resolver, uri(rootId), mimeType, name) ?: throw FileNotFoundException("cannot create $name")
        return DocumentsContract.getDocumentId(created)
    }

    override fun rename(relPath: String, newName: String): String {
        val renamed = DocumentsContract.renameDocument(resolver, uri(relPath), newName) ?: return relPath
        return DocumentsContract.getDocumentId(renamed)
    }

    override fun delete(relPath: String): Boolean = runCatching { DocumentsContract.deleteDocument(resolver, uri(relPath)) }.getOrDefault(false)

    override fun inputStream(relPath: String): InputStream = resolver.openInputStream(uri(relPath)) ?: throw FileNotFoundException(relPath)

    override fun outputStream(relPath: String): OutputStream = resolver.openOutputStream(uri(relPath), "wt") ?: throw FileNotFoundException(relPath)

    fun uri(documentId: String): Uri = DocumentsContract.buildDocumentUriUsingTree(treeUri, documentId)

    private fun query(uri: Uri): List<DocumentEntry> {
        val columns = arrayOf(Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME, Document.COLUMN_MIME_TYPE, Document.COLUMN_SIZE, Document.COLUMN_LAST_MODIFIED)
        val out = ArrayList<DocumentEntry>()
        resolver.query(uri, columns, null, null, null)?.use { c ->
            while (c.moveToNext()) {
                val mime = c.getString(2)
                out.add(DocumentEntry(
                    relPath = c.getString(0),
                    name = c.getString(1) ?: "",
                    isDirectory = mime == Document.MIME_TYPE_DIR,
                    size = if (c.isNull(3)) 0 else c.getLong(3),
                    modifiedAt = Instant.ofEpochMilli(if (c.isNull(4)) 0 else c.getLong(4)),
                    mimeType = mime,
                ))
            }
        }
        return out
    }

    companion object {
        const val NO_ACCESS = "no_access"
        const val MISSING = "missing"

        /** Keeps access to a picked folder across restarts. */
        fun persist(context: Context, treeUri: Uri) {
            context.contentResolver.takePersistableUriPermission(treeUri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        }

        /** "Internal storage › Documents › Hentai Reader" style name for Settings. */
        fun displayPath(treeUri: Uri, internalName: String): String {
            val id = DocumentsContract.getTreeDocumentId(treeUri)
            val volume = id.substringBefore(':')
            val path = id.substringAfter(':', "").split('/').filter { it.isNotEmpty() }
            return (listOf(if (volume == "primary") internalName else volume) + path).joinToString(" › ")
        }

        /** The folder picker starts at the shared Documents folder (007 FR-028). */
        val DOCUMENTS: Uri = DocumentsContract.buildDocumentUri("com.android.externalstorage.documents", "primary:Documents")
    }
}

/** Random access through a file descriptor (positional channel reads and writes). */
class FdSeekableSource(private val pfd: ParcelFileDescriptor, writable: Boolean) : SeekableSource {
    private val input = FileInputStream(pfd.fileDescriptor)
    private val output = if (writable) FileOutputStream(pfd.fileDescriptor) else null
    private val channel: FileChannel = input.channel
    private val writeChannel: FileChannel? = output?.channel

    override val size: Long get() = channel.size()

    override fun read(position: Long, buffer: ByteArray, offset: Int, length: Int): Int = channel.read(ByteBuffer.wrap(buffer, offset, length), position)

    override fun write(position: Long, buffer: ByteArray, offset: Int, length: Int) {
        val ch = writeChannel ?: throw UnsupportedOperationException("read-only")
        val buf = ByteBuffer.wrap(buffer, offset, length)
        var at = position
        while (buf.hasRemaining()) at += ch.write(buf, at)
        ch.force(true)
    }

    override fun truncate(size: Long) {
        (writeChannel ?: throw UnsupportedOperationException("read-only")).truncate(size)
    }

    override fun close() {
        runCatching { output?.close() }
        runCatching { input.close() }
        pfd.close()
    }
}

/**
 * A saved archive in a document tree. [replace] writes `<name>.rewrite`, then deletes the old
 * document and gives the new one its name (providers have no atomic replace).
 */
class SafArchiveFile(private val tree: SafDocumentTree, private val documentId: String) : ArchiveFile {
    override val name: String get() = tree.stat(documentId)?.name ?: documentId.substringAfterLast('/')
    override fun openRead() = tree.openRead(documentId)
    override fun openWrite() = tree.openWrite(documentId)

    override fun replace(write: (OutputStream) -> Unit) {
        val original = name
        val temp = tree.create("$original.rewrite", "application/octet-stream")
        try {
            tree.outputStream(temp).buffered().use(write)
        } catch (e: Throwable) {
            tree.delete(temp)
            throw e
        }
        tree.delete(documentId)
        tree.rename(temp, original)
    }
}
