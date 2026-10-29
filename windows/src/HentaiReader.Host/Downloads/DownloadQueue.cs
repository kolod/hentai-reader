using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Library;
using HentaiReader.Host.Reading;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Downloads;

/// <summary>A source the queue can download from: its running session and current setting values.</summary>
public sealed record DownloadSource(SourceSession Session, string Name, IReadOnlyDictionary<string, string>? Settings);

/// <summary>
/// Saves galleries into the library (specs/006-windows-library research R5): one worker per source,
/// every page through the source exactly as online reading does (page cache first, else getPageUrl +
/// download with retry hints and the stall timeout), staged on the target volume, then assembled into
/// one archive. Rate limits and sign-in requirements stop the download and keep its pages
/// (Constitution VI); a restart continues where it stopped. State changes are reported through
/// <see cref="Changed"/> (raised on worker threads).
/// </summary>
public sealed class DownloadQueue : IDisposable
{
    public const string StagingFolder = ".hentai-reader";

    private readonly LibraryService _library;
    private readonly Func<string, DownloadSource?> _sources;
    private readonly PageCache? _cache;
    private readonly ConcurrentDictionary<string, Worker> _workers = new();
    private readonly SemaphoreSlim _pump = new(1, 1);
    private bool _disposed;

    private sealed class Worker(long id, CancellationTokenSource cts)
    {
        public long Id { get; } = id;
        public CancellationTokenSource Cts { get; } = cts;
        public DownloadState? StopAs { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class PageAttempts : IPageAttempts
    {
        public JsonNode? PageImage { get; set; }
        public int Attempts { get; set; }
        public int? FailedStatus { get; set; }
    }

    public DownloadQueue(LibraryService library, Func<string, DownloadSource?> sources, PageCache? cache)
    {
        _library = library;
        _sources = sources;
        _cache = cache;
    }

    /// <summary>A download changed state or progress.</summary>
    public event Action<DownloadRow>? Changed;

    /// <summary>Stall timeout per image (tests use a short one).</summary>
    public TimeSpan ImageTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Downloads that were running when the app closed continue; then the queue starts.</summary>
    public async Task StartAsync(CancellationToken token = default)
    {
        foreach (var row in await _library.Downloads.ListAsync(token))
        {
            if (row.State is DownloadState.Downloading or DownloadState.Assembling)
                await _library.Downloads.SetStateAsync(row.Id, DownloadState.Queued, at: _library.Now, token: token);
        }
        await PumpAsync();
    }

    /// <summary>Queues a gallery (from getGallery) for the target library folder; returns the download id.</summary>
    public async Task<long> EnqueueAsync(string sourceId, JsonObject gallery, IReadOnlyDictionary<string, string>? settings, CancellationToken token = default)
    {
        var key = new GalleryKey(sourceId, gallery["id"]!.GetValue<string>());
        if (await _library.Downloads.ActiveForAsync(key, token) is { } existing)
        {
            if (existing.State is DownloadState.Paused or DownloadState.Limited or DownloadState.Failed) await ResumeAsync(existing.Id, token);
            return existing.Id;
        }
        var folder = await _library.TargetFolderAsync(token);
        var id = await _library.Downloads.AddAsync(new DownloadRow
        {
            Key = key,
            Title = gallery["title"]?.GetValue<string>() ?? key.GalleryId,
            GalleryJson = gallery.ToJsonString(),
            SettingsJson = JsonSerializer.Serialize(settings ?? new Dictionary<string, string>()),
            FolderId = folder.Id,
            PageCount = (gallery["pages"] as JsonArray)?.Count ?? 0,
            QueuedAt = _library.Now,
        }, token);
        await NotifyAsync(id);
        await PumpAsync();
        return id;
    }

    public async Task PauseAsync(long id, CancellationToken token = default)
    {
        if (StopWorker(id, DownloadState.Paused)) return;   // the worker records the state when it stops
        if (await _library.Downloads.GetAsync(id, token) is { State: DownloadState.Queued })
        {
            await _library.Downloads.SetStateAsync(id, DownloadState.Paused, at: _library.Now, token: token);
            await NotifyAsync(id);
        }
    }

    /// <summary>Paused, limited or failed → queued again (a rate limit is cleared: the user chose to retry).</summary>
    public async Task ResumeAsync(long id, CancellationToken token = default)
    {
        var row = await _library.Downloads.GetAsync(id, token);
        if (row is null || row.State is not (DownloadState.Paused or DownloadState.Limited or DownloadState.Failed)) return;
        if (row.State == DownloadState.Limited) _sources(row.Key.SourceId)?.Session.ClearLimit();
        await _library.Downloads.SetStateAsync(id, DownloadState.Queued, at: _library.Now, token: token);
        await NotifyAsync(id);
        await PumpAsync();
    }

    /// <summary>Stops the download and removes everything it staged; no partial file remains (FR-006).</summary>
    public async Task CancelAsync(long id, CancellationToken token = default)
    {
        if (_workers.Values.FirstOrDefault(w => w.Id == id) is { } worker)
        {
            worker.StopAs = null;
            worker.Cts.Cancel();
            try { await worker.Task; } catch (OperationCanceledException) { }
        }
        var row = await _library.Downloads.GetAsync(id, token);
        if (row is null) return;
        if (row.State != DownloadState.Done)
        {
            var folder = (await _library.Folders.ListAsync(token)).FirstOrDefault(f => f.Id == row.FolderId);
            if (folder is not null) DeleteStaging(folder.Path, id);
        }
        await _library.Downloads.DeleteAsync(id, token);
        Changed?.Invoke(row with { State = DownloadState.Done, PagesDone = -1 });   // PagesDone -1: removed
        await PumpAsync();
    }

    /// <summary>Starts a worker for every source that has queued downloads and none running.</summary>
    private async Task PumpAsync()
    {
        if (_disposed) return;
        await _pump.WaitAsync();
        try
        {
            foreach (var row in await _library.Downloads.ListAsync())
            {
                if (row.State != DownloadState.Queued || _workers.ContainsKey(row.Key.SourceId)) continue;
                var source = _sources(row.Key.SourceId);
                if (source is null || source.Session.State == SessionState.Limited) continue;
                var worker = new Worker(row.Id, new CancellationTokenSource());
                _workers[row.Key.SourceId] = worker;
                worker.Task = Task.Run(() => RunAsync(row, source, worker));
            }
        }
        finally
        {
            _pump.Release();
        }
    }

    private bool StopWorker(long id, DownloadState state)
    {
        var worker = _workers.Values.FirstOrDefault(w => w.Id == id);
        if (worker is null) return false;
        worker.StopAs = state;
        worker.Cts.Cancel();
        return true;
    }

    private async Task RunAsync(DownloadRow row, DownloadSource source, Worker worker)
    {
        var token = worker.Cts.Token;
        try
        {
            await _library.Downloads.SetStateAsync(row.Id, DownloadState.Downloading, at: _library.Now, token: token);
            await NotifyAsync(row.Id);
            var folder = (await _library.Folders.ListAsync(token)).First(f => f.Id == row.FolderId);
            var staging = StagingPath(folder.Path, row.Id);
            Directory.CreateDirectory(staging);
            HideStagingRoot(folder.Path);

            var gallery = JsonNode.Parse(row.GalleryJson)!.AsObject();
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(row.SettingsJson);
            var fingerprint = PageCache.Fingerprint(settings);
            var pages = gallery["pages"] as JsonArray ?? [];
            var done = (await _library.Downloads.PagesAsync(row.Id, token)).ToDictionary(p => p.Page);
            var missing = done.Values.Where(p => !File.Exists(Path.Combine(staging, p.File))).Select(p => p.Page).ToList();
            if (missing.Count > 0)
            {
                await _library.Downloads.RemovePagesAsync(row.Id, missing, token);
                foreach (var m in missing) done.Remove(m);
            }

            // As many pages at once as the source allows requests at once (its politeness still applies to each).
            var concurrency = Math.Clamp(source.Session.Manifest["politeness"]?["maxConcurrency"]?.GetValue<int>() ?? 1, 1, 4);
            var todo = Enumerable.Range(0, pages.Count).Where(i => !done.ContainsKey(i)).ToList();
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (index, ct) =>
            {
                var (bytes, located) = await FetchPageAsync(source, row.Key.SourceId, settings, gallery, index, fingerprint, ct);
                var extension = ImageFormat.Sniff(bytes) ?? "jpg";
                var size = ImageFormat.TryGetSize(bytes) ?? located ?? EstimatedSize(pages[index]);
                var file = $"{index}.{extension}";
                var path = Path.Combine(staging, file);
                await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
                File.Move(path + ".tmp", path, overwrite: true);
                var page = new DownloadPage(index, file, size.Width, size.Height);
                await _library.Downloads.AddPageAsync(row.Id, page, ct);
                lock (done) done[index] = page;
                await NotifyAsync(row.Id);
            });

            await _library.Downloads.SetStateAsync(row.Id, DownloadState.Assembling, at: _library.Now, token: token);
            await NotifyAsync(row.Id);
            if (done.Count < pages.Count) throw new InvalidDataException("pages missing after download");
            await AssembleAsync(row, source, folder, staging, gallery, [.. done.Values.OrderBy(p => p.Page)], token);
            DeleteStaging(folder.Path, row.Id);
            await _library.Downloads.SetStateAsync(row.Id, DownloadState.Done, at: _library.Now, token: CancellationToken.None);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (worker.StopAs is { } state) await _library.Downloads.SetStateAsync(row.Id, state, at: _library.Now);
        }
        catch (PageResolveException e) when (e.Error.Kind is SourceErrorKind.RateLimited or SourceErrorKind.LoginRequired)
        {
            await _library.Downloads.SetStateAsync(row.Id, DownloadState.Limited, e.Error.Kind, e.Error.Message,
                e.Error.RetryAfterSeconds is { } s ? _library.Now.AddSeconds(s) : null, _library.Now);
        }
        catch (SourceLimitedException e)
        {
            await _library.Downloads.SetStateAsync(row.Id, DownloadState.Limited, SourceErrorKind.RateLimited, e.Message,
                e.RetryAfterSeconds is { } s ? _library.Now.AddSeconds(s) : null, _library.Now);
        }
        catch (PageResolveException e)
        {
            await _library.Downloads.SetStateAsync(row.Id, DownloadState.Failed, e.Error.Type == "SourceError" ? e.Error.Kind : "ScriptError", e.Error.Message, at: _library.Now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException or InvalidDataException)
        {
            // Short reasons for the list; site URLs stay out of the UI.
            var (kind, message) = e switch
            {
                HttpRequestException { StatusCode: { } code } => ("Network", $"HTTP {(int)code}"),
                HttpRequestException or TimeoutException => ("Network", null),
                InvalidDataException => ("Network", null),
                _ => ("Disk", e.Message),
            };
            await _library.Downloads.SetStateAsync(row.Id, DownloadState.Failed, kind, message, at: _library.Now);
        }
        finally
        {
            _workers.TryRemove(new KeyValuePair<string, Worker>(row.Key.SourceId, worker));
            if (await _library.Downloads.GetAsync(row.Id) is not null) await NotifyAsync(row.Id);
            await PumpAsync();
        }
    }

    private async Task<(byte[] Bytes, (int Width, int Height)? Located)> FetchPageAsync(DownloadSource source, string sourceId, IReadOnlyDictionary<string, string>? settings,
        JsonObject gallery, int index, string fingerprint, CancellationToken token)
    {
        var cacheKey = PageCache.Key(sourceId, gallery["id"]!.GetValue<string>(), index, fingerprint);
        if (_cache is not null && _cache.TryGet(cacheKey, out var cached)) return (cached, null);
        (int, int)? located = null;
        var loader = new PageImageLoader(source.Session, settings) { Timeout = ImageTimeout };
        var bytes = await loader.LoadAsync(gallery, index, new PageAttempts(), image =>
        {
            if (image["width"]?.GetValue<double>() is > 0 and var w && image["height"]?.GetValue<double>() is > 0 and var h) located = ((int)w, (int)h);
        }, token);
        if (_cache is not null) await _cache.PutAsync(cacheKey, bytes, CancellationToken.None);
        return (bytes, located);
    }

    private static (int Width, int Height) EstimatedSize(JsonNode? page) =>
        ((int)Math.Max(1, page?["width"]?.GetValue<double>() ?? 2), (int)Math.Max(1, page?["height"]?.GetValue<double>() ?? 3));

    private async Task AssembleAsync(DownloadRow row, DownloadSource source, LibraryFolder folder, string staging, JsonObject g,
        IReadOnlyList<DownloadPage> pages, CancellationToken token)
    {
        var metadata = new ArchiveMetadata
        {
            App = "Hentai Reader (Windows)",
            Source = new() { Id = row.Key.SourceId, Name = source.Name, GalleryId = row.Key.GalleryId, WebUrl = g["webUrl"]?.GetValue<string>() },
            Title = row.Title,
            TitleOriginal = g["titleOriginal"]?.GetValue<string>(),
            Language = g["language"]?.GetValue<string>(),
            Type = g["type"]?.GetValue<string>(),
            Uploader = g["uploader"]?.GetValue<string>(),
            UploadedAt = DateTimeOffset.TryParse(g["uploadedAt"]?.GetValue<string>(), out var up) ? up : null,
            Tags = (g["tags"] as JsonArray ?? []).Select(t => new ArchiveMetadata.Tag(t?["namespace"]?.GetValue<string>(), t?["name"]?.GetValue<string>() ?? "")).Where(t => t.Name.Length > 0).ToList(),
            SavedAt = _library.Now,
            Pages = pages.Select(p => new ArchiveMetadata.Page(CbzWriter.PageName(p.Page, pages.Count, Path.GetExtension(p.File).TrimStart('.')), p.Width, p.Height)).ToList(),
        };
        if (await _library.Progress.GetAsync(row.Key, token) is { } progress)
            metadata.Progress = new(progress.Page, progress.Offset, progress.Finished, progress.UpdatedAt);
        if (await _library.ReaderSettings.GetAsync(row.Key, token) is { } reader)
            metadata.Reader = new(reader.Zoom, reader.FitWidth, reader.Gaps, reader.UpdatedAt);
        var file = await CbzWriter.WriteAsync(folder.Path, pages.Select(p => Path.Combine(staging, p.File)).ToList(), metadata, token);
        await _library.Archives.UpsertAsync(LibraryService.RowFor(folder.Id, folder.Path, file, metadata), CancellationToken.None);
    }

    private async Task NotifyAsync(long id)
    {
        if (Changed is null) return;
        if (await _library.Downloads.GetAsync(id) is { } row) Changed?.Invoke(row);
    }

    public static string StagingPath(string folder, long id) => Path.Combine(folder, StagingFolder, "partial", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void DeleteStaging(string folder, long id)
    {
        try
        {
            var path = StagingPath(folder, id);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            var root = Path.Combine(folder, StagingFolder);
            var partial = Path.Combine(root, "partial");
            if (Directory.Exists(partial) && !Directory.EnumerateFileSystemEntries(partial).Any()) Directory.Delete(partial);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (IOException)
        {
            // Left for the next cleanup; staging is never listed as a gallery.
        }
    }

    private static void HideStagingRoot(string folder)
    {
        try
        {
            var root = new DirectoryInfo(Path.Combine(folder, StagingFolder));
            if (root.Exists) root.Attributes |= FileAttributes.Hidden;
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _workers.Values)
        {
            w.StopAs = DownloadState.Queued;   // continues on the next start
            w.Cts.Cancel();
        }
    }
}
