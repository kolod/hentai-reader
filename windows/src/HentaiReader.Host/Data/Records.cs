using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Data;

/// <summary>Gallery identity (data-model.md): source + gallery id, or "local" + content id for foreign archives.</summary>
public readonly record struct GalleryKey(string SourceId, string GalleryId)
{
    public const string LocalSource = "local";
    public override string ToString() => $"{SourceId}|{GalleryId}";
}

public sealed record Progress(int Page, double Offset, int PageCount, bool Finished, DateTimeOffset UpdatedAt);

/// <summary>Reader settings remembered per gallery (FR-020).</summary>
public sealed record ReaderSettings(double Zoom, bool FitWidth, bool Gaps, DateTimeOffset UpdatedAt);

public sealed record HistoryEntry(GalleryKey Key, string Title, string? CoverFile, int PageCount, DateTimeOffset LastReadAt, Progress? Progress, bool Saved);

public sealed record LibraryFolder(long Id, string Path, bool IsTarget, DateTimeOffset AddedAt);

public enum ArchiveState { Ok, Unreadable, Unavailable }

public sealed record ArchivePage(string File, int Width, int Height);

public sealed record ArchiveTag(string? Namespace, string Name);

/// <summary>A saved gallery: one archive file in a library folder.</summary>
public sealed record ArchiveRow
{
    public long Id { get; init; }
    public required long FolderId { get; init; }
    public required string RelPath { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset ModifiedAt { get; init; }
    public required GalleryKey Key { get; init; }
    public required string Title { get; init; }
    public string? TitleOriginal { get; init; }
    public string? Language { get; init; }
    public string? Type { get; init; }
    public IReadOnlyList<ArchiveTag> Tags { get; init; } = [];
    public required IReadOnlyList<ArchivePage> Pages { get; init; }
    public required DateTimeOffset SavedAt { get; init; }
    public ArchiveState State { get; init; } = ArchiveState.Ok;
    /// <summary><c>hentai-reader-1</c> or <c>foreign</c>.</summary>
    public required string Format { get; init; }
    public bool MetadataDirty { get; init; }
}

/// <summary>A library list row: the archive plus its folder path and reading progress.</summary>
public sealed record LibraryItem(ArchiveRow Archive, string FolderPath, Progress? Progress)
{
    public string FullPath => System.IO.Path.Combine(FolderPath, Archive.RelPath);
}

public enum LibrarySort { Added, LastRead, Title }

public enum ReadFilter { All, Unread, InProgress, Finished }

public sealed record LibraryQuery(string? Text = null, string? SourceId = null, ReadFilter Read = ReadFilter.All, LibrarySort Sort = LibrarySort.Added, int Offset = 0, int Limit = 200);

public enum DownloadState { Queued, Downloading, Assembling, Paused, Limited, Failed, Done }

public sealed record DownloadRow
{
    public long Id { get; init; }
    public required GalleryKey Key { get; init; }
    public required string Title { get; init; }
    /// <summary>The gallery from getGallery at queue time (JSON).</summary>
    public required string GalleryJson { get; init; }
    /// <summary>Source setting values at queue time (JSON object).</summary>
    public string SettingsJson { get; init; } = "{}";
    public required long FolderId { get; init; }
    public DownloadState State { get; init; } = DownloadState.Queued;
    public required int PageCount { get; init; }
    public int PagesDone { get; init; }
    public string? ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? RetryAfter { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record DownloadPage(int Page, string File, int Width, int Height);

internal static class Sql
{
    public static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static string? NullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Escapes LIKE wildcards; used with <c>ESCAPE '\'</c>.</summary>
    public static string LikeContains(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
