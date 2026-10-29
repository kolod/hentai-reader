using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Tests.Storage;

/// <summary>Moving the page cache to another folder while it is in use (006 US7, FR-024, FR-025).</summary>
public sealed class CacheMoveTests : IDisposable
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("hr-cachemove-").FullName;
    private string A => Path.Combine(_dir, "a");
    private string B => Path.Combine(_dir, "b");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Bytes(int i) => Enumerable.Repeat((byte)i, 1000 + i).ToArray();

    private async Task<PageCache> FilledAsync(int count)
    {
        var cache = new PageCache(A, 100 * 1024 * 1024);
        for (var i = 0; i < count; i++) await cache.PutAsync($"k{i}", Bytes(i), Token);
        return cache;
    }

    [Fact]
    public async Task Reads_work_during_the_move_and_new_files_go_to_the_new_folder()
    {
        using var cache = await FilledAsync(30);
        cache.BeginMove(B);

        Assert.Equal((30, 30), cache.MoveProgress);
        Assert.True(cache.TryGet("k5", out var before));            // still in the old folder
        Assert.Equal(Bytes(5), before);
        await cache.PutAsync("new", Bytes(99), Token);
        Assert.Equal(31, Directory.GetFiles(B).Count(f => !f.EndsWith(".json")) + Directory.GetFiles(A).Count(f => !f.EndsWith(".json")) - 0);
        Assert.True(await cache.MoveStepAsync(10, Token));
        for (var i = 0; i < 30; i++)
        {
            Assert.True(cache.TryGet($"k{i}", out var b), $"k{i}");
            Assert.Equal(Bytes(i), b);
        }
        while (await cache.MoveStepAsync(10, Token)) { }

        Assert.Null(cache.MovingFrom);
        Assert.False(Directory.Exists(A));                            // emptied and removed
        Assert.True(cache.TryGet("new", out _));
    }

    [Fact]
    public async Task Move_resumes_after_a_restart()
    {
        var cache = await FilledAsync(20);
        cache.BeginMove(B);
        await cache.MoveStepAsync(5, Token);
        cache.Dispose();

        using var again = new PageCache(B, 100 * 1024 * 1024, moveFrom: A);
        Assert.Equal(15, again.MoveProgress.Remaining);
        for (var i = 0; i < 20; i++) Assert.True(again.TryGet($"k{i}", out _), $"k{i}");
        while (await again.MoveStepAsync(50, Token)) { }
        Assert.Equal(20, Directory.GetFiles(B).Count(f => !f.EndsWith(".json")));
    }

    [Fact]
    public async Task Cancel_moves_files_back()
    {
        using var cache = await FilledAsync(10);
        cache.BeginMove(B);
        await cache.MoveStepAsync(4, Token);

        cache.BeginMove(A);   // cancel: back to the old folder
        Assert.Equal(4, cache.MoveProgress.Remaining);
        while (await cache.MoveStepAsync(50, Token)) { }

        Assert.Equal(A, cache.Folder);
        Assert.Equal(10, Directory.GetFiles(A).Count(f => !f.EndsWith(".json")));
        for (var i = 0; i < 10; i++) Assert.True(cache.TryGet($"k{i}", out _));
    }

    [Fact]
    public async Task Failure_loses_no_file()
    {
        using var cache = await FilledAsync(10);
        cache.BeginMove(B);
        await cache.MoveStepAsync(3, Token);
        // The new folder becomes unusable (a file where the folder was = "folder gone / no space").
        Directory.Delete(B, recursive: true);
        await File.WriteAllTextAsync(B, "blocked", Token);

        await Assert.ThrowsAnyAsync<IOException>(() => cache.MoveStepAsync(10, Token));

        var readable = Enumerable.Range(0, 10).Count(i => cache.TryGet($"k{i}", out _));
        Assert.Equal(7, readable);   // the 3 moved ones were in the lost folder; the rest are intact
        Assert.Equal(7, Directory.GetFiles(A).Count(f => !f.EndsWith(".json")));
    }
}
