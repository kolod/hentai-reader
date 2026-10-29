using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HentaiReader.Host.Archive;

/// <summary>One central-directory record; <see cref="Raw"/> is its exact bytes (copied on metadata updates).</summary>
public sealed record ZipEntry(string Name, ushort Method, ushort Flags, uint Crc32, long CompressedSize, long Size, long LocalHeaderOffset, byte[] Raw);

/// <summary>
/// Minimal ZIP central-directory reader (ZIP64 aware). It uses the LAST valid end record, so bytes
/// appended after it by an interrupted metadata update are ignored and the previous archive stays
/// readable (archive format 1, "Updating").
/// </summary>
public sealed class ZipDirectory
{
    private const uint EndSignature = 0x06054b50, Zip64EndSignature = 0x06064b50, Zip64LocatorSignature = 0x07064b50;
    private const uint CentralSignature = 0x02014b50, LocalSignature = 0x04034b50;

    public required IReadOnlyList<ZipEntry> Entries { get; init; }
    public long CentralDirectoryOffset { get; init; }
    public long CentralDirectorySize { get; init; }
    /// <summary>Offset of the end-of-central-directory record in use.</summary>
    public long EndOffset { get; init; }
    /// <summary>Length of the end record including its comment (and the ZIP64 records before it, if any).</summary>
    public long EndLength { get; init; }
    public bool IsZip64 { get; init; }

    public static ZipDirectory Read(SafeFileHandle file)
    {
        var length = RandomAccess.GetLength(file);
        var tailLength = (int)Math.Min(length, 22 + 65535 + 1024 * 1024);
        var tail = new byte[tailLength];
        ReadExactly(file, tail, length - tailLength);
        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndSignature) continue;
            var endOffset = length - tailLength + i;
            if (TryRead(file, length, endOffset, tail.AsSpan(i), out var dir)) return dir!;
        }
        throw new InvalidDataException("not a ZIP archive (no valid end of central directory)");
    }

    private static bool TryRead(SafeFileHandle file, long length, long endOffset, ReadOnlySpan<byte> end, out ZipDirectory? dir)
    {
        dir = null;
        if (end.Length < 22) return false;
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(end[20..]);
        if (endOffset + 22 + commentLength > length) return false;
        long count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var zip64 = false;
        var start = endOffset;
        if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            if (endOffset < 20) return false;
            var locator = new byte[20];
            ReadExactly(file, locator, endOffset - 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature) return false;
            var z64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (z64Offset < 0 || z64Offset + 56 > length) return false;
            var z64 = new byte[56];
            ReadExactly(file, z64, z64Offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(z64) != Zip64EndSignature) return false;
            count = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(32));
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(40));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(48));
            zip64 = true;
            start = z64Offset;
        }
        if (cdOffset < 0 || cdSize < 0 || cdOffset + cdSize > start) return false;
        var cd = new byte[cdSize];
        ReadExactly(file, cd, cdOffset);
        var entries = new List<ZipEntry>((int)Math.Min(count, 100_000));
        var at = 0;
        for (long n = 0; n < count; n++)
        {
            if (at + 46 > cd.Length || BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(at)) != CentralSignature) return false;
            var r = cd.AsSpan(at);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(r[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(r[10..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
            long csize = BinaryPrimitives.ReadUInt32LittleEndian(r[20..]);
            long usize = BinaryPrimitives.ReadUInt32LittleEndian(r[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(r[30..]);
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(r[32..]);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(r[42..]);
            var recordLength = 46 + nameLength + extraLength + commentLen;
            if (at + recordLength > cd.Length) return false;
            var nameBytes = r.Slice(46, nameLength);
            var name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);
            ReadZip64Extra(r.Slice(46 + nameLength, extraLength), ref usize, ref csize, ref localOffset);
            entries.Add(new ZipEntry(name, method, flags, crc, csize, usize, localOffset, r[..recordLength].ToArray()));
            at += recordLength;
        }
        dir = new ZipDirectory
        {
            Entries = entries,
            CentralDirectoryOffset = cdOffset,
            CentralDirectorySize = cdSize,
            EndOffset = endOffset,
            EndLength = endOffset - start + 22 + commentLength,
            IsZip64 = zip64,
        };
        return true;
    }

    private static void ReadZip64Extra(ReadOnlySpan<byte> extra, ref long usize, ref long csize, ref long offset)
    {
        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (4 + size > extra.Length) return;
            if (id == 0x0001)
            {
                var data = extra.Slice(4, size);
                var p = 0;
                if (usize == 0xFFFFFFFF && p + 8 <= data.Length) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[p..]); p += 8; }
                if (csize == 0xFFFFFFFF && p + 8 <= data.Length) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[p..]); p += 8; }
                if (offset == 0xFFFFFFFF && p + 8 <= data.Length) offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[p..]);
                return;
            }
            extra = extra[(4 + size)..];
        }
    }

    /// <summary>Offset of an entry's data and the length of its local header.</summary>
    public static (long DataOffset, int HeaderLength) LocateData(SafeFileHandle file, ZipEntry entry)
    {
        var header = new byte[30];
        ReadExactly(file, header, entry.LocalHeaderOffset);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature) throw new InvalidDataException($"bad local header for {entry.Name}");
        var length = 30 + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        return (entry.LocalHeaderOffset + length, length);
    }

    public static void ReadExactly(SafeFileHandle file, Span<byte> buffer, long offset)
    {
        while (buffer.Length > 0)
        {
            var n = RandomAccess.Read(file, buffer, offset);
            if (n <= 0) throw new EndOfStreamException();
            buffer = buffer[n..];
            offset += n;
        }
    }
}
