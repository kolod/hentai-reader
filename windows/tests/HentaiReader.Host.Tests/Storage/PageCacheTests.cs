using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Tests.Storage;

public sealed class PageCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hr-cache-test-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-24T12:00:00Z"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static byte[] Bytes(int n, byte fill = 1) => Enumerable.Repeat(fill, n).ToArray();

    [Fact]
    public async Task Put_then_get_returns_the_bytes()
    {
        using var cache = new PageCache(_dir, 1_000, _time);
        var key = PageCache.Key("hitomi", "123", 4, "x");
        Assert.False(cache.TryGet(key, out _));
        await cache.PutAsync(key, Bytes(10, 7), Token);
        Assert.True(cache.TryGet(key, out var bytes));
        Assert.Equal(Bytes(10, 7), bytes);
        Assert.Equal(10, cache.SizeBytes);
    }

    [Fact]
    public void Fingerprint_separates_settings_and_ignores_order()
    {
        var a = PageCache.Fingerprint(new Dictionary<string, string> { ["image_quality"] = "resampled", ["site"] = "e-hentai" });
        var b = PageCache.Fingerprint(new Dictionary<string, string> { ["site"] = "e-hentai", ["image_quality"] = "resampled" });
        var c = PageCache.Fingerprint(new Dictionary<string, string> { ["site"] = "e-hentai", ["image_quality"] = "original" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(PageCache.Fingerprint(null), PageCache.Fingerprint(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task Least_recently_used_files_are_evicted_beyond_the_limit()
    {
        using var cache = new PageCache(_dir, 30, _time);
        await cache.PutAsync("a", Bytes(10), Token);
        _time.Advance(TimeSpan.FromSeconds(1));
        await cache.PutAsync("b", Bytes(10), Token);
        _time.Advance(TimeSpan.FromSeconds(1));
        await cache.PutAsync("c", Bytes(10), Token);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(cache.TryGet("a", out _));                 // a is now the most recent
        _time.Advance(TimeSpan.FromSeconds(1));
        await cache.PutAsync("d", Bytes(10), Token);

        Assert.False(cache.TryGet("b", out _));                // oldest use → evicted
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
        Assert.Equal(30, cache.SizeBytes);
    }

    [Fact]
    public async Task Lowering_the_limit_evicts_immediately()
    {
        using var cache = new PageCache(_dir, 100, _time);
        for (var i = 0; i < 5; i++)
        {
            await cache.PutAsync($"k{i}", Bytes(10), Token);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        cache.LimitBytes = 25;
        Assert.Equal(20, cache.SizeBytes);
        Assert.True(cache.TryGet("k4", out _));
        Assert.False(cache.TryGet("k0", out _));
    }

    [Fact]
    public async Task Index_survives_a_restart_and_is_rebuilt_when_corrupt()
    {
        using (var cache = new PageCache(_dir, 1_000, _time))
        {
            await cache.PutAsync("a", Bytes(5), Token);
            await cache.PutAsync("b", Bytes(7), Token);
        }
        using (var reopened = new PageCache(_dir, 1_000, _time))
        {
            Assert.Equal(12, reopened.SizeBytes);
            Assert.True(reopened.TryGet("b", out _));
        }

        File.WriteAllText(Path.Combine(_dir, "index.json"), "{ not json");
        using var rebuilt = new PageCache(_dir, 1_000, _time);
        Assert.Equal(12, rebuilt.SizeBytes);
        Assert.True(rebuilt.TryGet("a", out var a));
        Assert.Equal(5, a.Length);
    }

    [Fact]
    public async Task Files_deleted_outside_the_cache_are_forgotten()
    {
        using var cache = new PageCache(_dir, 1_000, _time);
        await cache.PutAsync("a", Bytes(5), Token);
        foreach (var f in Directory.GetFiles(_dir).Where(f => !f.EndsWith("index.json"))) File.Delete(f);
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(0, cache.SizeBytes);
    }

    [Fact]
    public async Task Clear_empties_only_the_cache_folder()
    {
        var neighbour = Path.Combine(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir) + "-progress.json");
        File.WriteAllText(neighbour, "{}");
        try
        {
            using var cache = new PageCache(_dir, 1_000, _time);
            await cache.PutAsync("a", Bytes(5), Token);
            cache.Clear();
            Assert.Equal(0, cache.SizeBytes);
            Assert.Empty(Directory.GetFiles(_dir));
            Assert.True(File.Exists(neighbour));
        }
        finally
        {
            File.Delete(neighbour);
        }
    }

    [Fact]
    public async Task Concurrent_puts_keep_the_size_consistent()
    {
        using var cache = new PageCache(_dir, 1_000_000, _time);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => cache.PutAsync($"k{i % 20}", Bytes(100), Token)));
        Assert.Equal(20 * 100, cache.SizeBytes);
        Assert.Equal(20, Directory.GetFiles(_dir).Count(f => !f.EndsWith(".json") && !f.EndsWith(".tmp")));
    }
}
