using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using HentaiReader.Host.Archive;
using HentaiReader.Host.Data;
using HentaiReader.Host.Downloads;
using HentaiReader.Host.Http;
using HentaiReader.Host.Library;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;
using HentaiReader.Host.Tests.Parity;
using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Tests.Downloads;

/// <summary>
/// Saving galleries (US1, US5) with the real ehentai.js against a fake site: viewer pages name an image
/// per page; images are tiny PNGs whose header carries the page size.
/// </summary>
public sealed class DownloadQueueTests : IAsyncDisposable
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private const string GalleryToken = "0123456789";
    private readonly string _dir = Directory.CreateTempSubdirectory("hr-dl-").FullName;
    private readonly List<SourceSession> _sessions = [];

    private sealed class Site : ITransport
    {
        public List<string> Requests { get; } = new();
        /// <summary>Status for viewer pages (e.g. 429) instead of a page.</summary>
        public int ViewerStatus { get; set; } = 200;
        /// <summary>Viewer requests answered before every further one fails with <see cref="ViewerStatus"/>.</summary>
        public int AllowViewers { get; set; } = int.MaxValue;
        /// <summary>Delay per image, to observe the queue while a download runs.</summary>
        public int ImageDelayMs { get; set; }
        private int _viewers;

        public Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default)
        {
            lock (Requests) Requests.Add(request.Url);
            var url = request.Url;
            if (url.StartsWith("https://e-hentai.org/s/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _viewers) > AllowViewers) return Task.FromResult(new RawResponse(ViewerStatus, [], []));
                var page = int.Parse(url[(url.LastIndexOf('-') + 1)..].Split('?')[0]);
                return Task.FromResult(new RawResponse(200, [new("content-type", "text/html")], Encoding.UTF8.GetBytes($"""
                    <div id="i2"><div>{page:000}.png :: {100 + page} x 150 :: 1 KiB</div></div>
                    <img id="img" src="https://a.hath.network/h/{page}.png" />
                    """)));
            }
            if (url.StartsWith("https://a.hath.network/h/", StringComparison.Ordinal))
            {
                var page = int.Parse(Path.GetFileNameWithoutExtension(url));
                return Delayed(new RawResponse(200, [new("content-type", "image/png")], Png(100 + page, 150)), token);
            }
            return Task.FromResult(new RawResponse(404, [], []));
        }

        private async Task<RawResponse> Delayed(RawResponse response, CancellationToken token)
        {
            if (ImageDelayMs > 0) await Task.Delay(ImageDelayMs, token);
            return response;
        }
    }

    public static byte[] Png(int width, int height)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)height);
        return b;
    }

    private static JsonObject Gallery(int pages) => new()
    {
        ["id"] = $"e/1234567/{GalleryToken}",
        ["title"] = "Saved gallery",
        ["language"] = "en",
        ["type"] = "manga",
        ["webUrl"] = $"https://e-hentai.org/g/1234567/{GalleryToken}/",
        ["tags"] = new JsonArray(new JsonObject { ["namespace"] = "other", ["name"] = "full color" }),
        ["pages"] = new JsonArray(Enumerable.Range(0, pages).Select(i => (JsonNode)new JsonObject { ["index"] = i, ["width"] = 2, ["height"] = 3 }).ToArray()),
        ["sourceData"] = new JsonObject { ["pages"] = new JsonArray(Enumerable.Range(1, pages).Select(p => (JsonNode)new JsonArray("abcdef0123", p)).ToArray()) },
    };

    private async Task<(LibraryService Library, DownloadQueue Queue, Site Site, PageCache Cache)> CreateAsync(Site? site = null, string? dbName = null)
    {
        site ??= new Site();
        var library = await LibraryService.OpenAsync(Path.Combine(_dir, dbName ?? "library.db"), Path.Combine(_dir, "none.json"), Path.Combine(_dir, "lib"), token: Token);
        var script = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Sources, "ehentai", "ehentai.js"), Token);
        var session = await SourceSession.OpenAsync(script, "ehentai.js", new SessionOptions(site, EnforcePoliteness: false, new SourceStorage()));
        _sessions.Add(session);
        var cache = new PageCache(Path.Combine(_dir, "cache"), 100 * 1024 * 1024);
        var queue = new DownloadQueue(library, id => id == "ehentai" ? new DownloadSource(session, "E-Hentai", null) : null, cache)
        {
            ImageTimeout = TimeSpan.FromSeconds(5),
        };
        return (library, queue, site, cache);
    }

    private static Task<DownloadRow> WaitFor(DownloadQueue queue, long id, params DownloadState[] states)
    {
        var tcs = new TaskCompletionSource<DownloadRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += row =>
        {
            if (row.Id == id && row.PagesDone >= 0 && states.Contains(row.State)) tcs.TrySetResult(row);
        };
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    [Fact]
    public async Task Saves_a_gallery_into_one_archive_with_exact_sizes()
    {
        var (library, queue, site, _) = await CreateAsync();
        using var _q = queue;
        var done = WaitFor(queue, 1, DownloadState.Done, DownloadState.Failed, DownloadState.Limited);

        var id = await queue.EnqueueAsync("ehentai", Gallery(5), null, Token);
        var row = await done;

        Assert.Equal((DownloadState.Done, 5), (row.State, row.PagesDone));
        var saved = await library.FindSavedAsync(new GalleryKey("ehentai", $"e/1234567/{GalleryToken}"), Token);
        Assert.NotNull(saved);
        Assert.Equal("Saved gallery.cbz", Path.GetFileName(saved!.FullPath));
        Assert.Equal([101, 102, 103, 104, 105], saved.Archive.Pages.Select(p => p.Width));   // exact, not the 2:3 estimate
        using (var reader = CbzReader.Open(saved.FullPath))
        {
            Assert.Equal(["0001.png", "0002.png", "0003.png", "0004.png", "0005.png"], reader.Pages.Select(p => p.Name));
            Assert.Equal(Png(104, 150), reader.ReadEntry(reader.Pages[3]));
            Assert.Equal("full color", reader.ReadMetadata()!.Tags.Single().Name);
        }
        Assert.False(Directory.Exists(Path.Combine(_dir, "lib", DownloadQueue.StagingFolder)));   // staging removed
        Assert.Equal(5, site.Requests.Count(u => u.Contains("hath.network")));
        Assert.Equal(id, row.Id);
    }

    [Fact]
    public async Task Pages_in_the_page_cache_are_not_requested()
    {
        var (_, queue, site, cache) = await CreateAsync();
        using var _q = queue;
        for (var i = 0; i < 3; i++) await cache.PutAsync(PageCache.Key("ehentai", $"e/1234567/{GalleryToken}", i, PageCache.Fingerprint(null)), Png(500 + i, 700), Token);
        var done = WaitFor(queue, 1, DownloadState.Done, DownloadState.Failed);

        await queue.EnqueueAsync("ehentai", Gallery(3), null, Token);

        Assert.Equal(DownloadState.Done, (await done).State);
        Assert.Empty(site.Requests);   // SC-002: everything came from the cache
    }

    [Fact]
    public async Task Rate_limit_stops_and_keeps_pages_then_resume_continues()
    {
        var site = new Site { AllowViewers = 2, ViewerStatus = 429 };
        var (library, queue, _, _) = await CreateAsync(site);
        using var _q = queue;
        var limited = WaitFor(queue, 1, DownloadState.Limited, DownloadState.Failed, DownloadState.Done);

        await queue.EnqueueAsync("ehentai", Gallery(5), null, Token);
        var row = await limited;

        Assert.Equal((DownloadState.Limited, 2, "RateLimited"), (row.State, row.PagesDone, row.ErrorKind));
        var viewersAtLimit = site.Requests.Count(u => u.Contains("/s/"));
        await Task.Delay(300, Token);
        Assert.Equal(viewersAtLimit, site.Requests.Count(u => u.Contains("/s/")));   // no hammering after the limit
        Assert.Null(await library.FindSavedAsync(new GalleryKey("ehentai", $"e/1234567/{GalleryToken}"), Token));

        site.AllowViewers = int.MaxValue;
        var done = WaitFor(queue, 1, DownloadState.Done, DownloadState.Failed, DownloadState.Limited);
        await queue.ResumeAsync(1, Token);
        Assert.Equal(DownloadState.Done, (await done).State);
        Assert.Equal(5, site.Requests.Count(u => u.Contains("hath.network")));   // pages 1–2 not fetched again
    }

    [Fact]
    public async Task Sign_in_required_stops_the_download()
    {
        var (library, queue, _, _) = await CreateAsync();
        using var _q = queue;
        var stopped = WaitFor(queue, 1, DownloadState.Limited, DownloadState.Failed, DownloadState.Done);

        // Original quality needs a signed-in E-Hentai account (the script checks and refuses).
        await queue.EnqueueAsync("ehentai", Gallery(2), new Dictionary<string, string> { ["image_quality"] = "original" }, Token);
        var row = await stopped;

        Assert.Equal((DownloadState.Limited, "LoginRequired"), (row.State, row.ErrorKind));
        Assert.Empty(await library.Archives.ListAsync(new(), Token));
    }

    [Fact]
    public async Task Restart_continues_without_fetching_pages_twice_and_cancel_leaves_nothing()
    {
        var site = new Site { AllowViewers = 3, ViewerStatus = 503 };
        var (library, queue, _, _) = await CreateAsync(site);
        var failed = WaitFor(queue, 1, DownloadState.Failed, DownloadState.Limited, DownloadState.Done);
        await queue.EnqueueAsync("ehentai", Gallery(6), null, Token);
        Assert.Equal(DownloadState.Failed, (await failed).State);
        queue.Dispose();
        // Simulate the app closing during the download.
        await library.Downloads.SetStateAsync(1, DownloadState.Downloading, token: Token);

        site.AllowViewers = int.MaxValue;
        var again = new DownloadQueue(library, id => new DownloadSource(_sessions[0], "E-Hentai", null), null);
        using var _a = again;
        var done = WaitFor(again, 1, DownloadState.Done, DownloadState.Failed);
        await again.StartAsync(Token);
        Assert.Equal(DownloadState.Done, (await done).State);
        var images = site.Requests.Where(u => u.Contains("hath.network")).ToList();
        Assert.Equal(images.Distinct().Count(), images.Count);   // SC-003: no page twice

        // Cancel: a second gallery paused mid-way leaves no staging and no archive.
        var other = Gallery(4);
        other["id"] = "e/7654321/9876543210";
        other["title"] = "Cancelled";
        site.AllowViewers = site.Requests.Count(u => u.Contains("/s/")) + 2;
        var stopped = WaitFor(again, 2, DownloadState.Failed, DownloadState.Done);
        var id2 = await again.EnqueueAsync("ehentai", other, null, Token);
        await stopped;
        await again.CancelAsync(id2, Token);
        Assert.Null(await library.Downloads.GetAsync(id2, Token));
        Assert.False(Directory.Exists(DownloadQueue.StagingPath(Path.Combine(_dir, "lib"), id2)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "lib"), "Cancelled*"));
    }

    [Fact]
    public async Task Pause_stops_requests_until_resume()
    {
        var (library, queue, site, _) = await CreateAsync();
        using var _q = queue;
        var paused = WaitFor(queue, 1, DownloadState.Paused, DownloadState.Done);
        var first = new TaskCompletionSource();
        queue.Changed += row => { if (row.PagesDone >= 1) first.TrySetResult(); };
        await queue.EnqueueAsync("ehentai", Gallery(40), null, Token);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        await queue.PauseAsync(1, Token);
        var row = await paused;
        if (row.State == DownloadState.Done) return;   // finished before the pause landed: nothing to check

        var requests = site.Requests.Count;
        await Task.Delay(300, Token);
        Assert.Equal(requests, site.Requests.Count);
        var done = WaitFor(queue, 1, DownloadState.Done);
        await queue.ResumeAsync(1, Token);
        Assert.Equal(40, (await done).PagesDone);
    }

    [Fact]
    public async Task One_download_per_source_others_wait_two_sources_run_together()
    {
        var site = new Site { ImageDelayMs = 150 };
        var (library, _, _, cache) = await CreateAsync(site);
        var session = _sessions[0];
        var second = await SourceSession.OpenAsync(await File.ReadAllTextAsync(Path.Combine(RepoPaths.Sources, "ehentai", "ehentai.js"), Token),
            "ehentai.js", new SessionOptions(site, EnforcePoliteness: false, new SourceStorage()));
        _sessions.Add(second);
        using var queue = new DownloadQueue(library, id => id switch
        {
            "a" => new DownloadSource(session, "A", null),
            "b" => new DownloadSource(second, "B", null),
            _ => null,
        }, null);
        var states = new System.Collections.Concurrent.ConcurrentQueue<(long Id, DownloadState State)>();
        queue.Changed += row => states.Enqueue((row.Id, row.State));
        var g2 = Gallery(4);
        g2["id"] = "e/2222222/2222222222";
        var g3 = Gallery(4);
        g3["id"] = "e/3333333/3333333333";
        var done = Task.WhenAll(WaitFor(queue, 1, DownloadState.Done), WaitFor(queue, 2, DownloadState.Done), WaitFor(queue, 3, DownloadState.Done));

        await queue.EnqueueAsync("a", Gallery(4), null, Token);
        await queue.EnqueueAsync("a", g2, null, Token);
        await queue.EnqueueAsync("b", g3, null, Token);
        await done;

        var order = states.ToList();
        var firstDone = order.FindIndex(s => s is { Id: 1, State: DownloadState.Done });
        var secondStarts = order.FindIndex(s => s is { Id: 2, State: DownloadState.Downloading });
        var otherSourceStarts = order.FindIndex(s => s is { Id: 3, State: DownloadState.Downloading });
        Assert.True(secondStarts > firstDone, "the second gallery of the same source waited");
        Assert.True(otherSourceStarts < firstDone, "the other source did not wait");
    }

    [Fact]
    public async Task Unusable_target_folder_fails_with_a_reason()
    {
        var (library, queue, _, _) = await CreateAsync();
        using var _q = queue;
        var target = (await library.Folders.ListAsync(Token)).Single();
        Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
        await File.WriteAllTextAsync(target.Path, "a file where the folder should be", Token);   // folder gone / not creatable
        var failed = WaitFor(queue, 1, DownloadState.Failed, DownloadState.Done, DownloadState.Limited);

        await queue.EnqueueAsync("ehentai", Gallery(2), null, Token);
        var row = await failed;

        Assert.Equal((DownloadState.Failed, "Disk"), (row.State, row.ErrorKind));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions) s.Dispose();
        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
