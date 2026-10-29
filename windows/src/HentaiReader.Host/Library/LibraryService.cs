using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;

namespace HentaiReader.Host.Library;

/// <summary>
/// The library as the app sees it (specs/006-windows-library): repositories over <c>library.db</c>,
/// lookups of saved copies, reading state shared by online and offline reading, and writing that
/// state back into saved archives (FR-009). Thread-safe; callers await off the UI thread.
/// </summary>
public sealed class LibraryService
{
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _flush = new(1, 1);

    private LibraryService(LibraryDatabase db, TimeProvider time)
    {
        Database = db;
        _time = time;
        Progress = new ProgressRepository(db);
        History = new HistoryRepository(db);
        ReaderSettings = new ReaderSettingsRepository(db);
        Folders = new FolderRepository(db);
        Archives = new ArchiveRepository(db);
        Downloads = new DownloadRepository(db);
    }

    public LibraryDatabase Database { get; }
    public ProgressRepository Progress { get; }
    public HistoryRepository History { get; }
    public ReaderSettingsRepository ReaderSettings { get; }
    public FolderRepository Folders { get; }
    public ArchiveRepository Archives { get; }
    public DownloadRepository Downloads { get; }
    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>
    /// Opens the database, imports the 005 <c>progress.json</c> once and makes sure at least one library
    /// folder exists (<paramref name="defaultFolder"/>; the directory itself is created on first save).
    /// </summary>
    public static async Task<LibraryService> OpenAsync(string databasePath, string progressJson, string defaultFolder, TimeProvider? time = null, CancellationToken token = default)
    {
        var db = await LibraryDatabase.OpenAsync(databasePath, token);
        var service = new LibraryService(db, time ?? TimeProvider.System);
        await db.ImportProgressJsonAsync(progressJson, token);
        if ((await service.Folders.ListAsync(token)).Count == 0) await service.Folders.AddAsync(defaultFolder, service.Now, token);
        return service;
    }

    /// <summary>The readable saved copy of a gallery, if any.</summary>
    public async Task<LibraryItem?> FindSavedAsync(GalleryKey key, CancellationToken token = default) =>
        (await Archives.FindAsync(key, token)).FirstOrDefault(i => i.Archive.State == ArchiveState.Ok);

    public async Task<LibraryFolder> TargetFolderAsync(CancellationToken token = default) =>
        (await Folders.ListAsync(token)).First(f => f.IsTarget);

    /// <summary>
    /// Stores the reading position and reader settings of a gallery (database first); saved copies are
    /// marked for a metadata update, done by <see cref="FlushMetadataAsync"/>.
    /// </summary>
    public async Task SaveReadingAsync(GalleryKey key, Progress progress, ReaderSettings settings, CancellationToken token = default)
    {
        await Progress.SetAsync(key, progress, token);
        await ReaderSettings.SetAsync(key, settings, token);
        foreach (var item in await Archives.FindAsync(key, token))
        {
            if (item.Archive.Format == "hentai-reader-1") await Archives.SetDirtyAsync(item.Archive.Id, true, token);
        }
    }

    /// <summary>
    /// Writes progress and reader settings into every marked archive. An archive that cannot be written
    /// now (read-only, in use, disk missing) stays marked and is tried again next time.
    /// </summary>
    public async Task<int> FlushMetadataAsync(CancellationToken token = default)
    {
        await _flush.WaitAsync(token);
        try
        {
            var written = 0;
            foreach (var item in await Archives.DirtyAsync(token))
            {
                try
                {
                    var progress = await Progress.GetAsync(item.Archive.Key, token);
                    var settings = await ReaderSettings.GetAsync(item.Archive.Key, token);
                    await Task.Run(() => WriteMetadata(item.FullPath, progress, settings), token);
                    var info = new FileInfo(item.FullPath);
                    await Archives.SetFileStampAsync(item.Archive.Id, info.Length, info.LastWriteTimeUtc, token);
                    await Archives.SetDirtyAsync(item.Archive.Id, false, token);
                    written++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    // Kept dirty; retried on the next flush.
                }
            }
            return written;
        }
        finally
        {
            _flush.Release();
        }
    }

    private static void WriteMetadata(string path, Progress? progress, ReaderSettings? settings)
    {
        ArchiveMetadata? metadata;
        using (var reader = CbzReader.Open(path)) metadata = reader.ReadMetadata();
        if (metadata is null) return;
        if (progress is not null) metadata.Progress = new(progress.Page, progress.Offset, progress.Finished, progress.UpdatedAt);
        if (settings is not null) metadata.Reader = new(settings.Zoom, settings.FitWidth, settings.Gaps, settings.UpdatedAt);
        MetadataUpdater.Update(path, metadata.ToUtf8());
    }

    /// <summary>The archive row for a written or found archive with this app's metadata.</summary>
    public static ArchiveRow RowFor(long folderId, string folderPath, string file, ArchiveMetadata m)
    {
        var info = new FileInfo(file);
        return new ArchiveRow
        {
            FolderId = folderId,
            RelPath = Path.GetRelativePath(folderPath, file),
            Size = info.Length,
            ModifiedAt = info.LastWriteTimeUtc,
            Key = new GalleryKey(m.Source.Id, m.Source.GalleryId),
            Title = m.Title,
            TitleOriginal = m.TitleOriginal,
            Language = m.Language,
            Type = m.Type,
            Tags = m.Tags.Select(t => new ArchiveTag(t.Namespace, t.Name)).ToList(),
            Pages = m.Pages.Select(p => new ArchivePage(p.File, p.Width, p.Height)).ToList(),
            SavedAt = m.SavedAt,
            Format = "hentai-reader-1",
        };
    }
}
