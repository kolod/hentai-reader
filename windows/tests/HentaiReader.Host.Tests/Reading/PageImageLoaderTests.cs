using System.Text;
using System.Text.Json.Nodes;
using HentaiReader.Host.Http;
using HentaiReader.Host.Reading;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;
using HentaiReader.Host.Tests.Parity;

namespace HentaiReader.Host.Tests.Reading;

/// <summary>
/// A stalled image server: the reader gives up after the timeout and asks the source for another
/// location. Runs the real E-Hentai script, whose RetryHint path reloads the viewer page with
/// <c>?nl=</c> and gets an image on another server.
/// </summary>
public class PageImageLoaderTests
{
    private const string Viewer = "https://e-hentai.org/s/0123456789/1234567-1";

    /// <summary>Viewer pages and image servers; images listed in <see cref="Stalled"/> never answer.</summary>
    private sealed class Site : ITransport
    {
        public HashSet<string> Stalled { get; } = new();
        /// <summary>Images answering HTTP 503 (a failing image server).</summary>
        public HashSet<string> Failing { get; } = new();
        public List<string> Requests { get; } = new();

        public async Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default)
        {
            lock (Requests) Requests.Add(request.Url);
            if (request.Url.StartsWith(Viewer, StringComparison.Ordinal))
            {
                // Each nl token leads to the next server: a.hath.network, then b, then c.
                var nl = request.Url.Contains("?nl=") ? request.Url[(request.Url.IndexOf("?nl=", StringComparison.Ordinal) + 4)..] : null;
                var server = nl switch { null => "a", "a-token" => "b", "b-token" => "c", _ => "z" };
                return Html($"""
                    <div id="i2"><div>001.webp :: 1280 x 1810 :: 210.4 KiB</div></div>
                    <img id="img" src="https://{server}.hath.network/h/abc/001.webp" />
                    <a href="#" id="loadfail" onclick="return nl('{server}-token')">Reload broken image</a>
                    """);
            }
            if (Stalled.Contains(request.Url)) await Task.Delay(Timeout.Infinite, token);
            if (Failing.Contains(request.Url)) return new RawResponse(503, [], []);
            return new RawResponse(200, [new("content-type", "image/webp")], Encoding.ASCII.GetBytes(request.Url));
        }

        private static RawResponse Html(string body) =>
            new(200, [new("content-type", "text/html; charset=utf-8")], Encoding.UTF8.GetBytes(body));
    }

    private sealed class Page : IPageAttempts
    {
        public JsonNode? PageImage { get; set; }
        public int Attempts { get; set; }
        public int? FailedStatus { get; set; }
    }

    private static readonly JsonObject Gallery = new()
    {
        ["id"] = "e/1234567/0123456789",
        ["sourceData"] = new JsonObject { ["pages"] = new JsonArray(new JsonArray("0123456789", 1)) },
    };

    private static async Task<SourceSession> Open(Site site)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Sources, "ehentai", "ehentai.js"), TestContext.Current.CancellationToken);
        return await SourceSession.OpenAsync(script, "ehentai.js", new SessionOptions(site, EnforcePoliteness: false, new SourceStorage()));
    }

    [Fact]
    public async Task Stalled_image_is_loaded_from_the_new_location_on_the_second_attempt()
    {
        var site = new Site();
        site.Stalled.Add("https://a.hath.network/h/abc/001.webp");
        using var session = await Open(site);
        var page = new Page();
        var resolved = new List<string>();

        var bytes = await new PageImageLoader(session) { Timeout = TimeSpan.FromMilliseconds(300) }
            .LoadAsync(Gallery, 0, page, image => resolved.Add(image["url"]!.GetValue<string>()), TestContext.Current.CancellationToken);

        Assert.Equal("https://b.hath.network/h/abc/001.webp", Encoding.ASCII.GetString(bytes));
        Assert.Equal(["https://a.hath.network/h/abc/001.webp", "https://b.hath.network/h/abc/001.webp"], resolved);
        Assert.Contains($"{Viewer}?nl=a-token", site.Requests);       // the second getPageUrl carried the RetryHint
        Assert.Equal(1, page.Attempts);
        Assert.Null(page.FailedStatus);
    }

    [Fact]
    public async Task Page_fails_with_a_timeout_after_the_automatic_retries()
    {
        var site = new Site();
        foreach (var server in new[] { "a", "b", "c" }) site.Stalled.Add($"https://{server}.hath.network/h/abc/001.webp");
        using var session = await Open(site);
        var page = new Page();

        var loader = new PageImageLoader(session) { Timeout = TimeSpan.FromMilliseconds(200), AutoRetries = 2 };
        await Assert.ThrowsAsync<TimeoutException>(() => loader.LoadAsync(Gallery, 0, page, null, TestContext.Current.CancellationToken));

        Assert.Equal(3, site.Requests.Count(u => u.Contains(".hath.network/")));   // first try + 2 retries
        Assert.Equal(2, page.Attempts);
        Assert.Equal("c-token", page.PageImage!["retryToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_error_is_retried_at_a_new_location_with_the_status()
    {
        var site = new Site();
        site.Failing.Add("https://a.hath.network/h/abc/001.webp");
        using var session = await Open(site);
        var page = new Page();
        var hints = new List<JsonNode?>();

        var bytes = await new PageImageLoader(session) { RetryDelay = TimeSpan.FromMilliseconds(10) }
            .LoadAsync(Gallery, 0, page, _ => hints.Add(PageImageLoader.RetryHint(page)), TestContext.Current.CancellationToken);

        Assert.Equal("https://b.hath.network/h/abc/001.webp", Encoding.ASCII.GetString(bytes));
        Assert.Equal(503, page.FailedStatus);   // sent as RetryHint.status with the second getPageUrl
        Assert.Contains($"{Viewer}?nl=a-token", site.Requests);
    }

    [Fact]
    public async Task Server_errors_everywhere_fail_after_the_retries()
    {
        var site = new Site();
        foreach (var server in new[] { "a", "b", "c" }) site.Failing.Add($"https://{server}.hath.network/h/abc/001.webp");
        using var session = await Open(site);

        var loader = new PageImageLoader(session) { RetryDelay = TimeSpan.FromMilliseconds(10) };
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync(Gallery, 0, new Page(), null, TestContext.Current.CancellationToken));

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, e.StatusCode);
        Assert.Equal(3, site.Requests.Count(u => u.Contains(".hath.network/")));
    }

    [Fact]
    public async Task Fast_image_needs_no_retry_hint()
    {
        var site = new Site();
        using var session = await Open(site);
        var page = new Page();

        var bytes = await new PageImageLoader(session).LoadAsync(Gallery, 0, page, null, TestContext.Current.CancellationToken);

        Assert.Equal("https://a.hath.network/h/abc/001.webp", Encoding.ASCII.GetString(bytes));
        Assert.DoesNotContain(site.Requests, u => u.Contains("?nl="));
        Assert.Equal(0, page.Attempts);
        Assert.Null(PageImageLoader.RetryHint(page));
    }
}
