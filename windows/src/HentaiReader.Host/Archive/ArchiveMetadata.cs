using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace HentaiReader.Host.Archive;

/// <summary>
/// <c>hentai-reader.json</c>, archive format 1 (contract/archive/README.md). Unknown fields are kept
/// when the file is read and written back, so a newer app's additions survive an update.
/// </summary>
public sealed class ArchiveMetadata
{
    public const string EntryName = "hentai-reader.json";
    public const string ComicInfoName = "ComicInfo.xml";
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public int Format { get; set; } = CurrentFormat;
    public string? App { get; set; }
    public required SourceInfo Source { get; set; }
    public required string Title { get; set; }
    public string? TitleOriginal { get; set; }
    public string? Language { get; set; }
    public string? Type { get; set; }
    public string? Uploader { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; }
    public List<Page> Pages { get; set; } = [];
    public ProgressBlock? Progress { get; set; }
    public ReaderBlock? Reader { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public sealed class SourceInfo
    {
        public required string Id { get; set; }
        public string? Name { get; set; }
        public required string GalleryId { get; set; }
        public string? WebUrl { get; set; }
    }

    public sealed record Tag(string? Namespace, string Name);

    public sealed record Page(string File, int Width, int Height);

    public sealed record ProgressBlock(int Page, double Offset, bool Finished, DateTimeOffset UpdatedAt);

    public sealed record ReaderBlock(double Zoom, bool FitWidth, bool Gaps, DateTimeOffset UpdatedAt);

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <summary>Parses format 1; returns null for another format (a newer app wrote it) or invalid JSON.</summary>
    public static ArchiveMetadata? Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ArchiveMetadata>(utf8, Options);
            return m is { Format: CurrentFormat } && m.Pages is not null ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Format number of a metadata file without full parsing (null when unreadable).</summary>
    public static int? FormatOf(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray());
            return doc.RootElement.TryGetProperty("format", out var f) && f.TryGetInt32(out var v) ? v : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>ComicInfo.xml (2.0 subset) for third-party readers; written once with the archive.</summary>
    public byte[] ComicInfoXml()
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("ComicInfo");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            Element(w, "Title", Title);
            Element(w, "Series", Title);
            Element(w, "Writer", Join("artist"));
            Element(w, "Penciller", Join("group"));
            Element(w, "Genre", Type);
            Element(w, "Tags", string.Join(", ", Tags.Select(t => string.IsNullOrEmpty(t.Namespace) ? t.Name : $"{t.Namespace}:{t.Name}")));
            Element(w, "Web", Source.WebUrl);
            Element(w, "LanguageISO", Language);
            Element(w, "PageCount", Pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteStartElement("Pages");
            for (var i = 0; i < Pages.Count; i++)
            {
                w.WriteStartElement("Page");
                w.WriteAttributeString("Image", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (i == 0) w.WriteAttributeString("Type", "FrontCover");
                w.WriteAttributeString("ImageWidth", Pages[i].Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteAttributeString("ImageHeight", Pages[i].Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return ms.ToArray();

        string Join(string ns) => string.Join(", ", Tags.Where(t => t.Namespace == ns).Select(t => t.Name));
    }

    private static void Element(XmlWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) w.WriteElementString(name, value);
    }
}
