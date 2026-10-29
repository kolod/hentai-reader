using System.IO.Compression;
using System.Text;

namespace HentaiReader.Host.Archive;

/// <summary>
/// Assembles a saved gallery (archive format 1): page images <c>NNNN.ext</c> in reading order, then
/// <c>ComicInfo.xml</c>, then <c>hentai-reader.json</c>; every entry stored, never recompressed. The
/// archive is written as <c>.cbz.part</c> and renamed when complete, so no half-written archive is
/// ever listed.
/// </summary>
public static class CbzWriter
{
    public const string Extension = ".cbz";
    public const string PartExtension = ".cbz.part";
    private const int MaxNameLength = 120;

    /// <summary>Entry name of a page: zero-padded to at least 4 digits (more for huge galleries).</summary>
    public static string PageName(int index, int pageCount, string extension) =>
        (index + 1).ToString(new string('0', Math.Max(4, pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length)),
            System.Globalization.CultureInfo.InvariantCulture) + "." + extension;

    /// <summary>
    /// Writes the archive into <paramref name="folder"/> and returns its path. <paramref name="pageFiles"/>
    /// are the staged page files in reading order; their names in <paramref name="metadata"/>.Pages are
    /// set here.
    /// </summary>
    public static async Task<string> WriteAsync(string folder, IReadOnlyList<string> pageFiles, ArchiveMetadata metadata, CancellationToken token = default)
    {
        if (pageFiles.Count != metadata.Pages.Count) throw new ArgumentException("one metadata page per page file", nameof(metadata));
        Directory.CreateDirectory(folder);
        var target = UniquePath(folder, SafeName(metadata.Title));
        var part = target[..^Extension.Length] + PartExtension;
        try
        {
            await using (var stream = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                for (var i = 0; i < pageFiles.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(metadata.Pages[i].File, CompressionLevel.NoCompression);
                    await using var to = entry.Open();
                    await using var from = File.OpenRead(pageFiles[i]);
                    await from.CopyToAsync(to, token);
                }
                await Add(zip, ArchiveMetadata.ComicInfoName, metadata.ComicInfoXml(), token);
                await Add(zip, ArchiveMetadata.EntryName, metadata.ToUtf8(), token);
            }
            File.Move(part, target);
            return target;
        }
        catch
        {
            File.Delete(part);
            throw;
        }
    }

    private static async Task Add(ZipArchive zip, string name, byte[] data, CancellationToken token)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        await using var s = entry.Open();
        await s.WriteAsync(data, token);
    }

    /// <summary>A file name from a title: characters Windows forbids replaced, trimmed, length-limited.</summary>
    public static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        if (name.Length == 0) name = "gallery";
        // Reserved device names (CON, NUL, COM1 …) cannot be file names.
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) name = "_" + name;
        return name;
    }

    /// <summary><c>name.cbz</c>, or <c>name (2).cbz</c>… when taken (also by a pending <c>.cbz.part</c>).</summary>
    public static string UniquePath(string folder, string name)
    {
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, (n == 1 ? name : $"{name} ({n})") + Extension);
            if (!File.Exists(candidate) && !File.Exists(candidate[..^Extension.Length] + PartExtension)) return candidate;
        }
    }
}
