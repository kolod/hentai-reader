package hentaireader.host.archive

import java.io.File
import java.io.OutputStream
import java.util.zip.CRC32
import java.util.zip.ZipEntry as JavaZipEntry
import java.util.zip.ZipOutputStream

/**
 * Writes a saved gallery (archive format 1; port of the Windows CbzWriter): page images
 * `NNNN.ext` in reading order, then `ComicInfo.xml`, then `hentai-reader.json`; every entry
 * stored, never recompressed. The caller writes into a `.cbz.partial` document and renames it when
 * complete, so no half-written archive is ever listed (007 research R6).
 */
object CbzWriter {
    const val EXTENSION = ".cbz"
    const val PART_EXTENSION = ".cbz.partial"
    private const val MAX_NAME_BYTES = 120

    /** Entry name of a page: zero-padded to at least 4 digits (more for huge galleries). */
    fun pageName(index: Int, pageCount: Int, extension: String): String =
        (index + 1).toString().padStart(maxOf(4, pageCount.toString().length), '0') + "." + extension

    /** Writes the archive to [out]; [pageFiles] are the staged pages, named as in [metadata].pages. */
    fun write(out: OutputStream, pageFiles: List<File>, metadata: ArchiveMetadata) {
        require(pageFiles.size == metadata.pages.size) { "one metadata page per page file" }
        ZipOutputStream(out.buffered(1 shl 16), Charsets.UTF_8).use { zip ->
            zip.setMethod(ZipOutputStream.STORED)
            pageFiles.forEachIndexed { i, file -> stored(zip, metadata.pages[i].file, file.readBytes()) }
            stored(zip, ArchiveMetadata.COMIC_INFO_NAME, metadata.comicInfoXml())
            stored(zip, ArchiveMetadata.ENTRY_NAME, metadata.toUtf8())
        }
    }

    /** Copies archive entries raw (STORED) and a new metadata file into a fresh archive (compaction). */
    fun rewrite(out: OutputStream, reader: CbzReader, keep: List<ZipEntry>, metadata: ByteArray) {
        ZipOutputStream(out.buffered(1 shl 16), Charsets.UTF_8).use { zip ->
            zip.setMethod(ZipOutputStream.STORED)
            for (e in keep.sortedBy { it.localHeaderOffset }) stored(zip, e.name, reader.readEntry(e))
            stored(zip, ArchiveMetadata.ENTRY_NAME, metadata)
        }
    }

    private fun stored(zip: ZipOutputStream, name: String, data: ByteArray) {
        val crc = CRC32().apply { update(data) }
        zip.putNextEntry(JavaZipEntry(name).apply {
            method = JavaZipEntry.STORED
            size = data.size.toLong()
            compressedSize = data.size.toLong()
            this.crc = crc.value
        })
        zip.write(data)
        zip.closeEntry()
    }

    /**
     * A file name from a title: the Windows rules plus those of FAT/exFAT cards (none of the
     * characters quote, asterisk, slash, colon, angle brackets, question mark, backslash, bar; no control characters, no trailing dot or space, at most 120 UTF-8 bytes, no reserved device
     * names), so an archive copies between Android and Windows unchanged (007 research R6).
     */
    fun safeName(title: String): String {
        val sb = StringBuilder(title.length)
        for (ch in title) sb.append(if (ch in "\"*/:<>?\\|" || ch.isISOControl()) '_' else ch)
        var name = sb.toString().trim().trimEnd('.', ' ')
        while (name.toByteArray(Charsets.UTF_8).size > MAX_NAME_BYTES) name = name.dropLast(1)
        name = name.trimEnd('.', ' ')
        if (Character.isHighSurrogate(name.lastOrNull() ?: 'a')) name = name.dropLast(1)
        if (name.isEmpty()) name = "gallery"
        val stem = name.substringBefore('.').uppercase()
        if (stem in setOf("CON", "PRN", "AUX", "NUL") || (stem.length == 4 && (stem.startsWith("COM") || stem.startsWith("LPT")) && stem[3].isDigit())) name = "_$name"
        return name
    }

    /** `name.cbz`, or `name (2).cbz`… when the name (or its pending `.cbz.partial`) is taken. */
    fun uniqueName(name: String, taken: Set<String>): String {
        var n = 1
        while (true) {
            val candidate = (if (n == 1) name else "$name ($n)") + EXTENSION
            if (candidate !in taken && candidate.removeSuffix(EXTENSION) + PART_EXTENSION !in taken) return candidate
            n++
        }
    }
}
