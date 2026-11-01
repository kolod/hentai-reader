package hentaireader.host.archive

import hentaireader.host.platform.SeekableSource
import java.io.EOFException
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** One central-directory record; [raw] is its exact bytes (copied on metadata updates). */
class ZipEntry(
    val name: String,
    val method: Int,
    val flags: Int,
    val crc32: Long,
    val compressedSize: Long,
    val size: Long,
    val localHeaderOffset: Long,
    val raw: ByteArray,
)

class InvalidArchiveException(message: String) : IOException(message)

/**
 * Minimal ZIP central-directory reader, ZIP64 aware (port of the Windows ZipDirectory). It uses
 * the LAST valid end record, so bytes appended after it by an interrupted metadata update are
 * ignored and the previous archive stays readable (archive format 1, "Updating").
 */
class ZipDirectory private constructor(
    val entries: List<ZipEntry>,
    val centralDirectoryOffset: Long,
    val centralDirectorySize: Long,
    /** Offset of the end-of-central-directory record in use. */
    val endOffset: Long,
    /** Length of the end record including its comment (and the ZIP64 records before it). */
    val endLength: Long,
    val isZip64: Boolean,
) {
    companion object {
        private const val END = 0x06054b50L
        private const val ZIP64_END = 0x06064b50L
        private const val ZIP64_LOCATOR = 0x07064b50L
        private const val CENTRAL = 0x02014b50L
        private const val LOCAL = 0x04034b50L

        fun read(file: SeekableSource): ZipDirectory {
            val length = file.size
            val tailLength = minOf(length, 22L + 65535 + 1024 * 1024).toInt()
            val tail = ByteArray(tailLength)
            readExactly(file, tail, length - tailLength)
            for (i in tailLength - 22 downTo 0) {
                if (u32(tail, i) != END) continue
                tryRead(file, length, length - tailLength + i, tail, i)?.let { return it }
            }
            throw InvalidArchiveException("not a ZIP archive (no valid end of central directory)")
        }

        private fun tryRead(file: SeekableSource, length: Long, endOffset: Long, end: ByteArray, at: Int): ZipDirectory? {
            if (end.size - at < 22) return null
            val commentLength = u16(end, at + 20)
            if (endOffset + 22 + commentLength > length) return null
            var count = u16(end, at + 10).toLong()
            var cdSize = u32(end, at + 12)
            var cdOffset = u32(end, at + 16)
            var zip64 = false
            var start = endOffset
            if (count == 0xFFFFL || cdSize == 0xFFFFFFFFL || cdOffset == 0xFFFFFFFFL) {
                if (endOffset < 20) return null
                val locator = ByteArray(20)
                readExactly(file, locator, endOffset - 20)
                if (u32(locator, 0) != ZIP64_LOCATOR) return null
                val z64Offset = u64(locator, 8)
                if (z64Offset < 0 || z64Offset + 56 > length) return null
                val z64 = ByteArray(56)
                readExactly(file, z64, z64Offset)
                if (u32(z64, 0) != ZIP64_END) return null
                count = u64(z64, 32)
                cdSize = u64(z64, 40)
                cdOffset = u64(z64, 48)
                zip64 = true
                start = z64Offset
            }
            if (cdOffset < 0 || cdSize < 0 || cdOffset + cdSize > start || cdSize > Int.MAX_VALUE) return null
            val cd = ByteArray(cdSize.toInt())
            readExactly(file, cd, cdOffset)
            val entries = ArrayList<ZipEntry>(minOf(count, 100_000L).toInt())
            var p = 0
            for (n in 0 until count) {
                if (p + 46 > cd.size || u32(cd, p) != CENTRAL) return null
                val flags = u16(cd, p + 8)
                val method = u16(cd, p + 10)
                val crc = u32(cd, p + 16)
                var csize = u32(cd, p + 20)
                var usize = u32(cd, p + 24)
                val nameLength = u16(cd, p + 28)
                val extraLength = u16(cd, p + 30)
                val commentLen = u16(cd, p + 32)
                var localOffset = u32(cd, p + 42)
                val recordLength = 46 + nameLength + extraLength + commentLen
                if (p + recordLength > cd.size) return null
                val name = String(cd, p + 46, nameLength, if (flags and 0x800 != 0) Charsets.UTF_8 else Charsets.ISO_8859_1)
                // ZIP64 extra field: the 0xFFFFFFFF sizes and offset follow in this order.
                var e = p + 46 + nameLength
                val extraEnd = e + extraLength
                while (e + 4 <= extraEnd) {
                    val id = u16(cd, e)
                    val size = u16(cd, e + 2)
                    if (e + 4 + size > extraEnd) break
                    if (id == 0x0001) {
                        var q = e + 4
                        val qEnd = q + size
                        if (usize == 0xFFFFFFFFL && q + 8 <= qEnd) { usize = u64(cd, q); q += 8 }
                        if (csize == 0xFFFFFFFFL && q + 8 <= qEnd) { csize = u64(cd, q); q += 8 }
                        if (localOffset == 0xFFFFFFFFL && q + 8 <= qEnd) localOffset = u64(cd, q)
                        break
                    }
                    e += 4 + size
                }
                entries.add(ZipEntry(name, method, flags, crc, csize, usize, localOffset, cd.copyOfRange(p, p + recordLength)))
                p += recordLength
            }
            return ZipDirectory(entries, cdOffset, cdSize, endOffset, endOffset - start + 22 + commentLength, zip64)
        }

        /** Offset of an entry's data and the length of its local header. */
        fun locateData(file: SeekableSource, entry: ZipEntry): Pair<Long, Int> {
            val header = ByteArray(30)
            readExactly(file, header, entry.localHeaderOffset)
            if (u32(header, 0) != LOCAL) throw InvalidArchiveException("bad local header for ${entry.name}")
            val length = 30 + u16(header, 26) + u16(header, 28)
            return entry.localHeaderOffset + length to length
        }

        fun readExactly(file: SeekableSource, buffer: ByteArray, offset: Long) {
            var done = 0
            while (done < buffer.size) {
                val n = file.read(offset + done, buffer, done, buffer.size - done)
                if (n <= 0) throw EOFException()
                done += n
            }
        }

        internal fun u16(b: ByteArray, i: Int) = (b[i].toInt() and 0xFF) or ((b[i + 1].toInt() and 0xFF) shl 8)
        internal fun u32(b: ByteArray, i: Int) = ByteBuffer.wrap(b, i, 4).order(ByteOrder.LITTLE_ENDIAN).int.toLong() and 0xFFFFFFFFL
        internal fun u64(b: ByteArray, i: Int) = ByteBuffer.wrap(b, i, 8).order(ByteOrder.LITTLE_ENDIAN).long
    }
}
