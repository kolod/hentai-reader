using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HentaiReader.Host.Archive;
using HentaiReader.Host.Tests.Parity;
using Json.Schema;

namespace HentaiReader.Host.Tests.Archive;

/// <summary>Archive format 1 (contract/archive/README.md): writing, reading, foreign archives, updates.</summary>
public sealed class ArchiveTests : IDisposable
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("hr-archive-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Webp(int seed, int size = 5000)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        "RIFF"u8.CopyTo(b);
        "WEBP"u8.CopyTo(b.AsSpan(8));
        return b;
    }

    private static ArchiveMetadata Metadata(int pages, string title = "Test: gallery? <1>") => new()
    {
        App = "tests",
        Source = new() { Id = "hitomi", Name = "Hitomi.la", GalleryId = "123", WebUrl = "https://hitomi.la/galleries/123.html" },
        Title = title,
        Language = "en",
        Type = "manga",
        Tags = [new("artist", "someone"), new("female", "glasses"), new(null, "full color")],
        SavedAt = DateTimeOffset.Parse("2026-10-25T09:00:00Z"),
        Pages = Enumerable.Range(0, pages).Select(i => new ArchiveMetadata.Page(CbzWriter.PageName(i, pages, "webp"), 1280, 1810)).ToList(),
        Progress = new(0, 0, false, DateTimeOffset.Parse("2026-10-25T09:00:00Z")),
        Reader = new(1, true, false, DateTimeOffset.Parse("2026-10-25T09:00:00Z")),
    };

    private async Task<(string Path, List<byte[]> Images)> SaveAsync(int pages = 3)
    {
        var images = new List<byte[]>();
        var files = new List<string>();
        for (var i = 0; i < pages; i++)
        {
            images.Add(Webp(i));
            files.Add(Path.Combine(_dir, $"staged{i}.webp"));
            await File.WriteAllBytesAsync(files[^1], images[^1], Token);
        }
        var path = await CbzWriter.WriteAsync(Path.Combine(_dir, "lib"), files, Metadata(pages), Token);
        return (path, images);
    }

    [Fact]
    public async Task Writes_stored_entries_in_order_with_valid_metadata()
    {
        var (path, images) = await SaveAsync();

        Assert.Equal("Test_ gallery_ _1_.cbz", Path.GetFileName(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part"));
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal(["0001.webp", "0002.webp", "0003.webp", "ComicInfo.xml", "hentai-reader.json"], zip.Entries.Select(e => e.FullName));
            Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
            var comicInfo = new StreamReader(zip.GetEntry("ComicInfo.xml")!.Open()).ReadToEnd();
            Assert.Contains("<Writer>someone</Writer>", comicInfo);
            Assert.Contains("ImageWidth=\"1280\"", comicInfo);
        }
        using (var reader = CbzReader.Open(path))
        {
            Assert.All(reader.Directory.Entries, e => Assert.Equal(0, e.Method));
            Assert.Equal(images, reader.Pages.Select(p => reader.ReadEntry(p)));
            var json = reader.ReadEntry(reader.Find(ArchiveMetadata.EntryName)!);
            var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(RepoPaths.Root, "contract", "archive", "hentai-reader.schema.json"), Token));
            var result = schema.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, string.Join("; ", result.Details?.Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")) ?? []));
            Assert.Equal("123", reader.ReadMetadata()!.Source.GalleryId);
        }
    }

    [Fact]
    public async Task Same_title_gets_a_second_name()
    {
        var (first, _) = await SaveAsync();
        var (second, _) = await SaveAsync();
        Assert.NotEqual(first, second);
        Assert.EndsWith(" (2).cbz", second);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("  dots...  ", "dots")]
    [InlineData("", "gallery")]
    public void Safe_names(string title, string expected) => Assert.Equal(expected, CbzWriter.SafeName(title));

    [Fact]
    public void Page_names_sort_in_reading_order()
    {
        Assert.Equal("0001.webp", CbzWriter.PageName(0, 30, "webp"));
        Assert.Equal("00001.jpg", CbzWriter.PageName(0, 12000, "jpg"));
        var names = Enumerable.Range(0, 12000).Select(i => CbzWriter.PageName(i, 12000, "png")).ToList();
        Assert.Equal(names, names.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif")]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 }, "avif")]
    [InlineData(new byte[] { 1, 2, 3 }, null)]
    public void Sniffs_image_formats(byte[] head, string? expected) => Assert.Equal(expected, ImageFormat.Sniff(head));

    [Fact]
    public void Foreign_archive_pages_in_natural_order_and_stable_content_id()
    {
        var path = Path.Combine(_dir, "foreign.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, seed) in new[] { ("10.jpg", 10), ("2.jpg", 2), ("cover.png", 0) })
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(Webp(seed, 20000));
            }
            using var t = new StreamWriter(zip.CreateEntry("notes.txt").Open());
            t.Write("not a page");
        }
        string id;
        using (var reader = CbzReader.Open(path))
        {
            Assert.Null(reader.ReadMetadata());
            Assert.Equal(["2.jpg", "10.jpg", "cover.png"], reader.Pages.Select(p => p.Name));
            Assert.Equal(Webp(10, 20000), reader.ReadEntry(reader.Pages[1]));   // deflated entry
            id = reader.ContentId();
        }
        var moved = Path.Combine(_dir, "renamed.cbz");
        File.Copy(path, moved);
        using (var reader = CbzReader.Open(moved)) Assert.Equal(id, reader.ContentId());
    }

    [Fact]
    public async Task Update_appends_metadata_and_keeps_images()
    {
        var (path, images) = await SaveAsync();
        var before = new FileInfo(path).Length;
        var m = Metadata(3);
        m.Progress = new(2, 0.5, true, DateTimeOffset.Parse("2026-10-25T10:00:00Z"));
        m.Reader = new(1.3, false, true, DateTimeOffset.Parse("2026-10-25T10:00:00Z"));

        Assert.Equal(MetadataUpdater.Result.Appended, MetadataUpdater.Update(path, m.ToUtf8()));

        Assert.True(new FileInfo(path).Length > before);
        using (var reader = CbzReader.Open(path))
        {
            Assert.Equal(images, reader.Pages.Select(p => reader.ReadEntry(p)));
            var read = reader.ReadMetadata()!;
            Assert.Equal((2, true, 1.3, true), (read.Progress!.Page, read.Progress.Finished, read.Reader!.Zoom, read.Reader.Gaps));
        }
        using var zip = ZipFile.OpenRead(path);   // other ZIP readers see the updated archive
        Assert.Equal(5, zip.Entries.Count);
        Assert.Contains("\"zoom\": 1.3", new StreamReader(zip.GetEntry(ArchiveMetadata.EntryName)!.Open()).ReadToEnd());
    }

    [Fact]
    public async Task Interrupted_update_leaves_the_previous_archive()
    {
        var (path, images) = await SaveAsync();
        var original = await File.ReadAllBytesAsync(path, Token);
        var m = Metadata(3);
        m.Progress = new(1, 0, false, DateTimeOffset.Parse("2026-10-25T10:00:00Z"));
        MetadataUpdater.Update(path, m.ToUtf8());
        var updated = await File.ReadAllBytesAsync(path, Token);

        // Cut the appended part at many points (a crash while writing it).
        for (var cut = original.Length + 1; cut < updated.Length; cut += Math.Max(1, (updated.Length - original.Length) / 25))
        {
            var copy = Path.Combine(_dir, $"cut-{cut}.cbz");
            await File.WriteAllBytesAsync(copy, updated.AsSpan(0, cut).ToArray(), Token);
            using var reader = CbzReader.Open(copy);
            Assert.Equal(images, reader.Pages.Select(p => reader.ReadEntry(p)));
            Assert.Equal(0, reader.ReadMetadata()!.Progress!.Page);
        }
        // And an update after such a crash still works.
        var damaged = Path.Combine(_dir, "damaged.cbz");
        await File.WriteAllBytesAsync(damaged, updated.AsSpan(0, updated.Length - 7).ToArray(), Token);
        m.Progress = new(2, 0, false, DateTimeOffset.Parse("2026-10-25T11:00:00Z"));
        MetadataUpdater.Update(damaged, m.ToUtf8());
        using (var reader = CbzReader.Open(damaged)) Assert.Equal(2, reader.ReadMetadata()!.Progress!.Page);
    }

    [Fact]
    public async Task Many_updates_are_compacted()
    {
        var (path, images) = await SaveAsync();
        var original = new FileInfo(path).Length;
        var m = Metadata(3);
        m.Extra = new() { ["padding"] = JsonSerializer.SerializeToElement(new string('x', 100_000)) };
        var results = new List<MetadataUpdater.Result>();
        for (var i = 0; i < 15; i++)
        {
            m.Progress = new(i % 3, 0, false, DateTimeOffset.UtcNow);
            results.Add(MetadataUpdater.Update(path, m.ToUtf8()));
        }

        Assert.Contains(MetadataUpdater.Result.Rewritten, results);
        Assert.True(new FileInfo(path).Length < original + 1_200_000);
        using var reader = CbzReader.Open(path);
        Assert.Equal(images, reader.Pages.Select(p => reader.ReadEntry(p)));
        Assert.Equal(14 % 3, reader.ReadMetadata()!.Progress!.Page);
        Assert.Equal(new string('x', 100_000), reader.ReadMetadata()!.Extra!["padding"].GetString());   // unknown fields survive
        Assert.All(reader.Directory.Entries, e => Assert.Equal(0, e.Method));
    }

    [Fact]
    public async Task Newer_format_is_not_overwritten()
    {
        var (path, _) = await SaveAsync();
        MetadataUpdater.Update(path, Encoding.UTF8.GetBytes("""{ "format": 2, "title": "future" }"""));   // a newer app wrote it
        using (var reader = CbzReader.Open(path)) Assert.Null(reader.ReadMetadata());
        Assert.Throws<InvalidOperationException>(() => MetadataUpdater.Update(path, Metadata(3).ToUtf8()));
    }

    [Fact]
    public void Crc32_matches_zip()
    {
        Assert.Equal(0xCBF43926u, MetadataUpdater.Crc32("123456789"u8));
    }
}
