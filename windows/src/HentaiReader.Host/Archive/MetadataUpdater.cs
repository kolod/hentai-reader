using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HentaiReader.Host.Archive;

/// <summary>
/// Replaces <c>hentai-reader.json</c> inside an archive without rewriting its images (archive format 1,
/// "Updating"): a new stored entry, a new central directory and a new end record are appended. Until
/// the new end record is complete, readers still find the previous one, so an interrupted update
/// leaves the previous archive intact. When unreferenced bytes exceed <see cref="CompactThreshold"/>,
/// or the archive needs ZIP64, the archive is rewritten into a temporary file and atomically replaced.
/// </summary>
public static class MetadataUpdater
{
    public const long CompactThreshold = 1024 * 1024;

    public enum Result { Appended, Rewritten }

    public static Result Update(string path, byte[] metadata)
    {
        ZipDirectory dir;
        long length;
        using (var probe = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            dir = ZipDirectory.Read(probe);
            length = RandomAccess.GetLength(probe);
        }
        var existing = dir.Entries.FirstOrDefault(e => e.Name == ArchiveMetadata.EntryName);
        if (existing is not null && ExistingFormatIsNewer(path, existing)) throw new InvalidOperationException("the archive's metadata has a newer format; it is not rewritten");

        var kept = dir.Entries.Where(e => e.Name != ArchiveMetadata.EntryName).ToList();
        var dead = length - LiveBytes(path, dir);
        var appendedEnd = length + 30 + Encoding.UTF8.GetByteCount(ArchiveMetadata.EntryName) + metadata.Length;
        var needsZip64 = dir.IsZip64 || kept.Count + 1 >= 0xFFFF || appendedEnd + 1024 * 1024 >= 0xFFFFFFFF;
        if (needsZip64 || dead + metadata.Length > CompactThreshold)
        {
            Rewrite(path, dir, kept, metadata);
            return Result.Rewritten;
        }
        Append(path, length, kept, metadata);
        return Result.Appended;
    }

    private static bool ExistingFormatIsNewer(string path, ZipEntry entry)
    {
        using var reader = CbzReader.Open(path);
        return ArchiveMetadata.FormatOf(reader.ReadEntry(entry)) is > ArchiveMetadata.CurrentFormat;
    }

    /// <summary>Bytes still referenced: every live entry's local header + data, the directory and the end record.</summary>
    private static long LiveBytes(string path, ZipDirectory dir)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long live = dir.CentralDirectorySize + dir.EndLength;
        foreach (var e in dir.Entries)
        {
            var (_, header) = ZipDirectory.LocateData(file, e);
            live += header + e.CompressedSize + ((e.Flags & 0x8) != 0 ? 16 : 0);
        }
        return live;
    }

    private static void Append(string path, long at, IReadOnlyList<ZipEntry> kept, byte[] metadata)
    {
        var name = Encoding.UTF8.GetBytes(ArchiveMetadata.EntryName);
        var crc = Crc32(metadata);
        var (time, date) = DosTime(DateTime.Now);
        var local = new byte[30 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(6), 0x800);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(10), time);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(12), date);
        BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)name.Length);
        name.CopyTo(local, 30);

        var central = new byte[46 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(central, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(6), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(8), 0x800);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(12), time);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(14), date);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(16), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(20), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(24), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(28), (ushort)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(42), (uint)at);
        name.CopyTo(central, 46);

        using var ms = new MemoryStream();
        ms.Write(local);
        ms.Write(metadata);
        var cdOffset = at + ms.Length;
        foreach (var e in kept) ms.Write(e.Raw);
        ms.Write(central);
        var cdSize = at + ms.Length - cdOffset;
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)(kept.Count + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)(kept.Count + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)cdSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)cdOffset);
        ms.Write(end);

        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        file.Position = at;
        file.Write(ms.GetBuffer(), 0, (int)ms.Length);
        file.Flush(flushToDisk: true);
    }

    /// <summary>Copies every kept entry (raw, never recompressed) and the new metadata into a fresh archive, then replaces the old one.</summary>
    private static void Rewrite(string path, ZipDirectory dir, IReadOnlyList<ZipEntry> kept, byte[] metadata)
    {
        var temp = path + ".rewrite";
        try
        {
            using (var reader = CbzReader.Open(path))
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var e in kept.OrderBy(e => e.LocalHeaderOffset))
                {
                    var entry = zip.CreateEntry(e.Name, CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(reader.ReadEntry(e));
                }
                var meta = zip.CreateEntry(ArchiveMetadata.EntryName, CompressionLevel.NoCompression);
                using (var s = meta.Open()) s.Write(metadata);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private static (ushort Time, ushort Date) DosTime(DateTime t) =>
        ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((Math.Max(t.Year, 1980) - 1980) << 9) | (t.Month << 5) | t.Day));

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
