using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HentaiReader.Host.Archive;

/// <summary>
/// Read access to a saved gallery or any comic archive: page entries in reading order, entry bytes
/// (stored entries straight from the file, deflated ones through a decompressor), this app's
/// metadata when present, and a content id for archives without it. Reads are thread-safe.
/// </summary>
public sealed class CbzReader : IDisposable
{
    private readonly SafeFileHandle _file;

    private CbzReader(string path, SafeFileHandle file, ZipDirectory directory)
    {
        Path = path;
        _file = file;
        Directory = directory;
        Pages = directory.Entries
            .Where(e => !e.Name.EndsWith('/') && !e.Name.StartsWith("__MACOSX/", StringComparison.Ordinal) && ImageFormat.IsImageName(e.Name))
            .OrderBy(e => e.Name, NaturalComparer.Instance)
            .ToList();
    }

    public string Path { get; }
    public ZipDirectory Directory { get; }
    /// <summary>Page entries in reading order (natural file-name order).</summary>
    public IReadOnlyList<ZipEntry> Pages { get; }

    public static CbzReader Open(string path)
    {
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            return new CbzReader(path, file, ZipDirectory.Read(file));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public ZipEntry? Find(string name) => Directory.Entries.FirstOrDefault(e => e.Name == name);

    /// <summary>This app's metadata, or null (foreign archive, unknown format or unreadable entry).</summary>
    public ArchiveMetadata? ReadMetadata()
    {
        var entry = Find(ArchiveMetadata.EntryName);
        return entry is null || entry.Size > 16 * 1024 * 1024 ? null : ArchiveMetadata.Parse(ReadEntry(entry));
    }

    public byte[] ReadEntry(ZipEntry entry)
    {
        if (entry.Size > int.MaxValue) throw new InvalidDataException($"{entry.Name} is too large");
        var (offset, _) = ZipDirectory.LocateData(_file, entry);
        var data = new byte[entry.Size];
        switch (entry.Method)
        {
            case 0:
                ZipDirectory.ReadExactly(_file, data, offset);
                break;
            case 8:
            {
                using var raw = new FileStream(new SafeFileHandle(_file.DangerousGetHandle(), ownsHandle: false), FileAccess.Read, 1, isAsync: false);
                raw.Position = offset;
                using var inflate = new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true);
                inflate.ReadExactly(data);
                break;
            }
            default:
                throw new InvalidDataException($"{entry.Name}: unsupported compression method {entry.Method}");
        }
        return data;
    }

    public Task<byte[]> ReadEntryAsync(ZipEntry entry, CancellationToken token = default) => Task.Run(() => ReadEntry(entry), token);

    /// <summary>The first bytes of an entry (image headers for size measurement) without reading it all.</summary>
    public byte[] ReadEntryPrefix(ZipEntry entry, int maxBytes)
    {
        var length = (int)Math.Min(entry.Size, maxBytes);
        var (offset, _) = ZipDirectory.LocateData(_file, entry);
        var data = new byte[length];
        if (entry.Method == 0)
        {
            ZipDirectory.ReadExactly(_file, data, offset);
            return data;
        }
        if (entry.Method != 8) throw new InvalidDataException($"{entry.Name}: unsupported compression method {entry.Method}");
        using var raw = new FileStream(new SafeFileHandle(_file.DangerousGetHandle(), ownsHandle: false), FileAccess.Read, 1, isAsync: false);
        raw.Position = offset;
        using var inflate = new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true);
        inflate.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Identity of an archive without this app's metadata: SHA-256 over the page entries' names, sizes
    /// and CRCs, so renaming or moving the file keeps its history (data-model "Gallery identity").
    /// </summary>
    public string ContentId()
    {
        var listing = new StringBuilder();
        foreach (var e in Pages.OrderBy(e => e.Name, StringComparer.Ordinal)) listing.Append(e.Name).Append('\0').Append(e.Size).Append('\0').Append(e.Crc32).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString())))[..32].ToLowerInvariant();
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>"2.jpg" before "10.jpg": digit runs compare by value, the rest ordinal ignoring case.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.CompareOrdinal(x, y);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.SequenceCompareTo(b);
                if (c != 0) return c;
                continue;
            }
            var d = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (d != 0) return d;
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j) is var r && r != 0 ? r : string.CompareOrdinal(x, y);
    }
}
