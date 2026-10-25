using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HentaiReader.Host.Http;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;
using HentaiReader.Host.Tests.Parity;

namespace HentaiReader.Host.Tests.Sources;

/// <summary>
/// Hitomi has no "any of these languages" search; the source reads the all-language feed in Range
/// chunks and keeps galleries from any requested language list (contract 1.2 multi filter). Runs the
/// real hitomi.js against synthetic nozomi lists: whole language lists would be too large as
/// recorded fixtures.
/// </summary>
public partial class HitomiLanguagesTests
{
    private const string Ltn = "https://ltn.gold-usergeneratedcontent.net";

    private sealed partial class Site : ITransport
    {
        /// <summary>The feed: ids 1000…1, newest first.</summary>
        public static readonly int[] Feed = Enumerable.Range(1, 1000).Reverse().ToArray();
        public static readonly int[] English = Feed.Where(id => id % 3 == 0).ToArray();
        public static readonly int[] Korean = Feed.Where(id => id % 5 == 0).ToArray();

        public List<string> Requests { get; } = new();

        public Task<RawResponse> SendAsync(RawRequest request, CancellationToken token = default)
        {
            lock (Requests) Requests.Add(request.Url);
            return Task.FromResult(request.Url switch
            {
                $"{Ltn}/n/index-all.nozomi" => Range(Feed, request.Headers.GetValueOrDefault("range")),
                $"{Ltn}/n/index-english.nozomi" => Nozomi(English),
                $"{Ltn}/n/index-korean.nozomi" => Nozomi(Korean),
                $"{Ltn}/gg.js" => new RawResponse(200, [new("content-type", "application/javascript")],
                    Encoding.UTF8.GetBytes("gg = { m: function (g) { return 0; }, s: function (h) { return '1'; }, b: \"1000000000/\" };")),
                _ => new RawResponse(404, [], []),   // gallery details: not needed here
            });
        }

        private static byte[] Bytes(IEnumerable<int> ids)
        {
            var list = ids.ToArray();
            var bytes = new byte[list.Length * 4];
            for (var i = 0; i < list.Length; i++) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 4), list[i]);
            return bytes;
        }

        private static RawResponse Nozomi(int[] ids) => new(200, [], Bytes(ids));

        private static RawResponse Range(int[] ids, string? range)
        {
            var all = Bytes(ids);
            var m = RangePattern().Match(range ?? "");
            if (!m.Success) return new RawResponse(200, [], all);
            var from = int.Parse(m.Groups[1].Value);
            if (from >= all.Length) return new RawResponse(416, [], []);
            var to = Math.Min(int.Parse(m.Groups[2].Value), all.Length - 1);
            return new RawResponse(206, [new("content-range", $"bytes {from}-{to}/{all.Length}")], all[from..(to + 1)]);
        }

        [GeneratedRegex(@"^bytes=(\d+)-(\d+)$")]
        private static partial Regex RangePattern();
    }

    private static async Task<SourceSession> Open(Site site)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(RepoPaths.Sources, "hitomi", "hitomi.js"), TestContext.Current.CancellationToken);
        return await SourceSession.OpenAsync(script, "hitomi.js", new SessionOptions(site, EnforcePoliteness: false, new SourceStorage()));
    }

    private static int[] GalleriesAsked(Site site, int skip) =>
        site.Requests.Skip(skip).Select(u => GalleryPattern().Match(u)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToArray();

    [GeneratedRegex(@"/galleries/(\d+)\.js$")]
    private static partial Regex GalleryPattern();

    [Fact]
    public async Task Feed_in_any_of_two_languages_keeps_the_site_order_and_pages_without_repeats()
    {
        var site = new Site();
        using var session = await Open(site);
        var expected = Site.Feed.Where(id => id % 3 == 0 || id % 5 == 0).ToArray();
        var query = JsonNode.Parse("""{ "text": "", "filters": { "language": ["en", "ko"] } }""");

        var first = await session.CallAsync("search", [query, null], token: TestContext.Current.CancellationToken);
        Assert.True(first.Ok, first.Error?.Message);
        Assert.Equal(expected[..25], GalleriesAsked(site, 0));
        Assert.Equal(Site.English.Length + Site.Korean.Length, first.Value!["total"]!.GetValue<int>());   // languages do not overlap on the site
        var cursor = first.Value["nextCursor"]!.GetValue<string>();
        Assert.Equal($"b:{Array.IndexOf(Site.Feed, expected[24]) + 1}", cursor);

        var asked = site.Requests.Count;
        var second = await session.CallAsync("search", [query!.DeepClone(), cursor], token: TestContext.Current.CancellationToken);
        Assert.True(second.Ok, second.Error?.Message);
        Assert.Equal(expected[25..50], GalleriesAsked(site, asked));
    }

    [Fact]
    public async Task One_language_in_a_list_uses_that_language_feed()
    {
        var site = new Site();
        using var session = await Open(site);

        var page = await session.CallAsync("search", [JsonNode.Parse("""{ "text": "", "filters": { "language": ["en"] } }"""), null], token: TestContext.Current.CancellationToken);

        Assert.True(page.Ok, page.Error?.Message);
        Assert.Contains($"{Ltn}/n/index-english.nozomi", site.Requests);
        Assert.DoesNotContain($"{Ltn}/n/index-all.nozomi", site.Requests);
        Assert.Equal(Site.English[..25], GalleriesAsked(site, 0));
    }

    [Fact]
    public async Task Bad_cursor_and_unknown_language_are_rejected()
    {
        var site = new Site();
        using var session = await Open(site);

        var badCursor = await session.CallAsync("search", [JsonNode.Parse("""{ "text": "", "filters": { "language": ["en", "ko"] } }"""), "o:25"], token: TestContext.Current.CancellationToken);
        var badLanguage = await session.CallAsync("search", [JsonNode.Parse("""{ "text": "", "filters": { "language": ["en", "xx"] } }"""), null], token: TestContext.Current.CancellationToken);

        Assert.Equal("InvalidInput", badCursor.Error?.Kind);
        Assert.Equal("InvalidInput", badLanguage.Error?.Kind);
    }
}
