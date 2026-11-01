package hentaireader.host.archive

import hentaireader.host.platform.ArchiveFile
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.LocalDateTime
import java.util.zip.CRC32

/**
 * Replaces `hentai-reader.json` inside an archive without rewriting its images (archive format 1,
 * "Updating"; port of the Windows MetadataUpdater): a new stored entry, a new central directory
 * and a new end record are appended. Until the new end record is complete, readers still find the
 * previous one, so an interrupted update leaves the previous archive intact. When unreferenced
 * bytes exceed [COMPACT_THRESHOLD], or the archive needs ZIP64, it is rewritten as a whole.
 */
object MetadataUpdater {
    const val COMPACT_THRESHOLD = 1024L * 1024

    enum class Result { APPENDED, REWRITTEN }

    fun update(archive: ArchiveFile, metadata: ByteArray): Result {
        val dir: ZipDirectory
        val length: Long
        val live: Long
        archive.openRead().use { src ->
            dir = ZipDirectory.read(src)
            length = src.size
            val existing = dir.entries.firstOrNull { it.name == ArchiveMetadata.ENTRY_NAME }
            if (existing != null) {
                val format = ArchiveMetadata.formatOf(CbzReaderNoClose(src).readEntry(existing))
                check(format == null || format <= ArchiveMetadata.CURRENT_FORMAT) { "the archive's metadata has a newer format; it is not rewritten" }
            }
            live = dir.centralDirectorySize + dir.endLength + dir.entries.sumOf { e ->
                ZipDirectory.locateData(src, e).second + e.compressedSize + if (e.flags and 0x8 != 0) 16 else 0
            }
        }
        val kept = dir.entries.filter { it.name != ArchiveMetadata.ENTRY_NAME }
        val dead = length - live
        val appendedEnd = length + 30 + ArchiveMetadata.ENTRY_NAME.toByteArray().size + metadata.size
        val needsZip64 = dir.isZip64 || kept.size + 1 >= 0xFFFF || appendedEnd + 1024 * 1024 >= 0xFFFFFFFFL
        if (needsZip64 || dead + metadata.size > COMPACT_THRESHOLD) {
            // The source is read and closed inside the write step, before the new file replaces it.
            archive.replace { out -> archive.openRead().use { src -> CbzWriter.rewrite(out, CbzReaderNoClose(src).reader, kept, metadata) } }
            return Result.REWRITTEN
        }
        archive.openWrite().use { dst -> dst.write(length, append(length, kept, metadata)) }
        return Result.APPENDED
    }

    private fun append(at: Long, kept: List<ZipEntry>, metadata: ByteArray): ByteArray {
        val name = ArchiveMetadata.ENTRY_NAME.toByteArray(Charsets.UTF_8)
        val crc = CRC32().apply { update(metadata) }.value
        val (time, date) = dosTime(LocalDateTime.now())
        val local = le(30 + name.size).apply {
            putInt(0x04034b50); putShort(20); putShort(0x800); putShort(0); putShort(time); putShort(date)
            putInt(crc.toInt()); putInt(metadata.size); putInt(metadata.size); putShort(name.size.toShort()); putShort(0); put(name)
        }.array()
        val central = le(46 + name.size).apply {
            putInt(0x02014b50); putShort(20); putShort(20); putShort(0x800); putShort(0); putShort(time); putShort(date)
            putInt(crc.toInt()); putInt(metadata.size); putInt(metadata.size); putShort(name.size.toShort())
            putShort(0); putShort(0); putShort(0); putShort(0); putInt(0); putInt(at.toInt()); put(name)
        }.array()
        val out = ByteArrayOutputStream()
        out.write(local)
        out.write(metadata)
        val cdOffset = at + out.size()
        for (e in kept) out.write(e.raw)
        out.write(central)
        val cdSize = at + out.size() - cdOffset
        val count = (kept.size + 1).toShort()
        out.write(le(22).apply {
            putInt(0x06054b50); putShort(0); putShort(0); putShort(count); putShort(count); putInt(cdSize.toInt()); putInt(cdOffset.toInt()); putShort(0)
        }.array())
        return out.toByteArray()
    }

    private fun le(n: Int) = ByteBuffer.allocate(n).order(ByteOrder.LITTLE_ENDIAN)

    private fun dosTime(t: LocalDateTime): Pair<Short, Short> =
        ((t.hour shl 11) or (t.minute shl 5) or (t.second / 2)).toShort() to
            (((maxOf(t.year, 1980) - 1980) shl 9) or (t.monthValue shl 5) or t.dayOfMonth).toShort()

    /** A reader over a source the caller closes. */
    private class CbzReaderNoClose(src: hentaireader.host.platform.SeekableSource) {
        val reader = CbzReader(object : hentaireader.host.platform.SeekableSource by src {
            override fun close() {}
        })

        fun readEntry(e: ZipEntry) = reader.readEntry(e)
    }
}
