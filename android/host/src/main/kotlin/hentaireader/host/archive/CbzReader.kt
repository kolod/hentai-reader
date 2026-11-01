package hentaireader.host.archive

import hentaireader.host.platform.SeekableSource
import java.security.MessageDigest
import java.util.zip.Inflater

/**
 * Read access to a saved gallery or any comic archive (port of the Windows CbzReader): page
 * entries in reading order, entry bytes (stored entries straight from the file, deflated ones
 * inflated), this app's metadata when present, and a content id for archives without it.
 */
class CbzReader(private val file: SeekableSource) : AutoCloseable {
    val directory = ZipDirectory.read(file)

    /** Page entries in reading order (natural file-name order). */
    val pages: List<ZipEntry> = directory.entries
        .filter { !it.name.endsWith('/') && !it.name.startsWith("__MACOSX/") && ImageFormat.isImageName(it.name) }
        .sortedWith { a, b -> NaturalComparator.compare(a.name, b.name) }

    fun find(name: String) = directory.entries.firstOrNull { it.name == name }

    /** This app's metadata, or null (foreign archive, unknown format or unreadable entry). */
    fun readMetadata(): ArchiveMetadata? {
        val entry = find(ArchiveMetadata.ENTRY_NAME) ?: return null
        if (entry.size > 16 * 1024 * 1024) return null
        return ArchiveMetadata.parse(readEntry(entry))
    }

    fun readEntry(entry: ZipEntry): ByteArray {
        if (entry.size > Int.MAX_VALUE) throw InvalidArchiveException("${entry.name} is too large")
        val (offset, _) = ZipDirectory.locateData(file, entry)
        return when (entry.method) {
            0 -> ByteArray(entry.size.toInt()).also { ZipDirectory.readExactly(file, it, offset) }
            8 -> {
                val compressed = ByteArray(entry.compressedSize.toInt()).also { ZipDirectory.readExactly(file, it, offset) }
                val out = ByteArray(entry.size.toInt())
                val inflater = Inflater(true)
                try {
                    inflater.setInput(compressed)
                    var done = 0
                    while (done < out.size) {
                        val n = inflater.inflate(out, done, out.size - done)
                        if (n == 0 && (inflater.finished() || inflater.needsInput())) break
                        done += n
                    }
                    if (done != out.size) throw InvalidArchiveException("${entry.name}: truncated data")
                } finally {
                    inflater.end()
                }
                out
            }
            else -> throw InvalidArchiveException("${entry.name}: unsupported compression method ${entry.method}")
        }
    }

    /** The first bytes of an entry (image headers for size measurement). */
    fun readEntryPrefix(entry: ZipEntry, maxBytes: Int): ByteArray {
        if (entry.method != 0) return readEntry(entry).copyOf(minOf(maxBytes.toLong(), entry.size).toInt())
        val (offset, _) = ZipDirectory.locateData(file, entry)
        return ByteArray(minOf(entry.size, maxBytes.toLong()).toInt()).also { ZipDirectory.readExactly(file, it, offset) }
    }

    /**
     * Identity of an archive without this app's metadata: SHA-256 over the page entries' names,
     * sizes and CRCs, so renaming or moving the file keeps its history (006 data-model).
     */
    fun contentId(): String {
        val listing = StringBuilder()
        for (e in pages.sortedBy { it.name }) listing.append(e.name).append('\u0000').append(e.size).append('\u0000').append(e.crc32).append('\n')
        return MessageDigest.getInstance("SHA-256").digest(listing.toString().toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }.take(32)
    }

    override fun close() = file.close()
}

/** "2.jpg" before "10.jpg": digit runs compare by value, the rest ordinal ignoring case (as on Windows). */
object NaturalComparator : Comparator<String> {
    override fun compare(x: String, y: String): Int {
        var i = 0
        var j = 0
        while (i < x.length && j < y.length) {
            if (x[i] in '0'..'9' && y[j] in '0'..'9') {
                val si = i
                while (i < x.length && x[i] in '0'..'9') i++
                val sj = j
                while (j < y.length && y[j] in '0'..'9') j++
                val a = x.substring(si, i).trimStart('0')
                val b = y.substring(sj, j).trimStart('0')
                val c = if (a.length != b.length) a.length.compareTo(b.length) else a.compareTo(b)
                if (c != 0) return c
                continue
            }
            val d = x[i].lowercaseChar().compareTo(y[j].lowercaseChar())
            if (d != 0) return d
            i++
            j++
        }
        val r = (x.length - i).compareTo(y.length - j)
        return if (r != 0) r else x.compareTo(y)
    }
}
