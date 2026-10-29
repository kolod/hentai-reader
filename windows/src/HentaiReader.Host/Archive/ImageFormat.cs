using System.Buffers.Binary;

namespace HentaiReader.Host.Archive;

/// <summary>Image type from the bytes themselves (sources sometimes serve another format than the URL says).</summary>
public static class ImageFormat
{
    public static readonly string[] Extensions = ["webp", "jpg", "png", "gif", "avif"];

    /// <summary>File extension without the dot, or null when the bytes are not a known image.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "webp";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return "gif";
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8) && (b[8..12].SequenceEqual("avif"u8) || b[8..12].SequenceEqual("avis"u8))) return "avif";
        return null;
    }

    /// <summary>Whether an archive entry name looks like a page image.</summary>
    public static bool IsImageName(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return ext is "webp" or "jpg" or "jpeg" or "png" or "gif" or "avif";
    }

    /// <summary>
    /// Pixel size from the image header without decoding (PNG, GIF, JPEG, WebP); null when unknown
    /// (AVIF and damaged data are measured by the platform decoder instead).
    /// </summary>
    public static (int Width, int Height)? TryGetSize(ReadOnlySpan<byte> b)
    {
        switch (Sniff(b))
        {
            case "png" when b.Length >= 24:
                return Positive((int)BinaryPrimitives.ReadUInt32BigEndian(b[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(b[20..]));
            case "gif" when b.Length >= 10:
                return Positive(BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));
            case "jpg":
                return JpegSize(b);
            case "webp" when b.Length >= 30:
            {
                var chunk = b[12..16];
                if (chunk.SequenceEqual("VP8 "u8) && b.Length >= 30)
                    return Positive(BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
                if (chunk.SequenceEqual("VP8L"u8) && b.Length >= 25)
                {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
                    return Positive((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
                }
                if (chunk.SequenceEqual("VP8X"u8))
                    return Positive((b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1);
                return null;
            }
            default:
                return null;
        }
    }

    private static (int, int)? JpegSize(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            // SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return Positive(BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
            i += 2 + length;
        }
        return null;
    }

    private static (int, int)? Positive(int w, int h) => w > 0 && h > 0 ? (w, h) : null;
}
