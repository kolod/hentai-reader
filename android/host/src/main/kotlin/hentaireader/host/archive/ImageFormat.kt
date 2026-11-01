package hentaireader.host.archive

/** Image type and size from the bytes themselves (port of the Windows ImageFormat). */
object ImageFormat {
    val EXTENSIONS = listOf("webp", "jpg", "png", "gif", "avif")

    private fun at(b: ByteArray, i: Int, s: String) = b.size >= i + s.length && s.indices.all { b[i + it] == s[it].code.toByte() }

    /** File extension without the dot, or null when the bytes are not a known image. */
    fun sniff(b: ByteArray): String? = when {
        at(b, 0, "RIFF") && at(b, 8, "WEBP") -> "webp"
        b.size >= 3 && b[0] == 0xFF.toByte() && b[1] == 0xD8.toByte() && b[2] == 0xFF.toByte() -> "jpg"
        b.size >= 8 && b[0] == 0x89.toByte() && at(b, 1, "PNG\r\n\u001A\n") -> "png"
        at(b, 0, "GIF87a") || at(b, 0, "GIF89a") -> "gif"
        at(b, 4, "ftyp") && (at(b, 8, "avif") || at(b, 8, "avis")) -> "avif"
        else -> null
    }

    /** Whether an archive entry name looks like a page image. */
    fun isImageName(name: String) = name.substringAfterLast('.', "").lowercase() in setOf("webp", "jpg", "jpeg", "png", "gif", "avif")

    /** Pixel size from the header without decoding (PNG, GIF, JPEG, WebP); null when unknown. */
    fun size(b: ByteArray): Pair<Int, Int>? = when (sniff(b)) {
        "png" -> if (b.size >= 24) positive(be32(b, 16), be32(b, 20)) else null
        "gif" -> if (b.size >= 10) positive(le16(b, 6), le16(b, 8)) else null
        "jpg" -> jpeg(b)
        "webp" -> when {
            b.size < 30 -> null
            at(b, 12, "VP8 ") -> positive(le16(b, 26) and 0x3FFF, le16(b, 28) and 0x3FFF)
            at(b, 12, "VP8L") -> {
                val bits = (b[21].toLong() and 0xFF) or ((b[22].toLong() and 0xFF) shl 8) or ((b[23].toLong() and 0xFF) shl 16) or ((b[24].toLong() and 0xFF) shl 24)
                positive((bits and 0x3FFF).toInt() + 1, ((bits shr 14) and 0x3FFF).toInt() + 1)
            }
            at(b, 12, "VP8X") -> positive(le24(b, 24) + 1, le24(b, 27) + 1)
            else -> null
        }
        else -> null
    }

    private fun jpeg(b: ByteArray): Pair<Int, Int>? {
        var i = 2
        while (i + 9 < b.size) {
            if (b[i] != 0xFF.toByte()) return null
            val marker = b[i + 1].toInt() and 0xFF
            if (marker == 0xFF) { i++; continue }
            if (marker == 0xD8 || marker == 0x01 || marker in 0xD0..0xD7) { i += 2; continue }
            val length = be16(b, i + 2)
            // SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size.
            if (marker in 0xC0..0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC) return positive(be16(b, i + 7), be16(b, i + 5))
            i += 2 + length
        }
        return null
    }

    private fun positive(w: Int, h: Int) = if (w > 0 && h > 0) w to h else null
    private fun le16(b: ByteArray, i: Int) = (b[i].toInt() and 0xFF) or ((b[i + 1].toInt() and 0xFF) shl 8)
    private fun le24(b: ByteArray, i: Int) = le16(b, i) or ((b[i + 2].toInt() and 0xFF) shl 16)
    private fun be16(b: ByteArray, i: Int) = ((b[i].toInt() and 0xFF) shl 8) or (b[i + 1].toInt() and 0xFF)
    private fun be32(b: ByteArray, i: Int) = (be16(b, i) shl 16) or be16(b, i + 2)
}
