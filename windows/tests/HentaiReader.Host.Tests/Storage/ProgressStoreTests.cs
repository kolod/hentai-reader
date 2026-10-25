using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Tests.Storage;

public sealed class ProgressStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hr-progress-test-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "progress.json");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-24T12:00:00Z");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Round_trip_through_the_file()
    {
        new ProgressStore(File_).Set("hitomi", "123", new ReadingProgress(39, 0.25, 120, false, At, "Title"));
        var loaded = new ProgressStore(File_).Get("hitomi", "123");
        Assert.Equal(new ReadingProgress(39, 0.25, 120, false, At, "Title"), loaded);
        Assert.Null(new ProgressStore(File_).Get("ehentai", "123"));
    }

    [Fact]
    public void Read_stays_true_once_set()
    {
        var store = new ProgressStore(File_);
        store.Set("s", "g", new ReadingProgress(119, 1, 120, true, At, "t"));
        store.Set("s", "g", new ReadingProgress(3, 0, 120, false, At, "t"));
        Assert.True(store.Get("s", "g")!.Read);
        Assert.Equal(3, store.Get("s", "g")!.Page);
    }

    [Fact]
    public void Values_are_clamped()
    {
        var store = new ProgressStore(File_);
        store.Set("s", "g", new ReadingProgress(500, 7, 10, false, At, "t"));
        Assert.Equal((9, 1.0), (store.Get("s", "g")!.Page, store.Get("s", "g")!.Offset));
    }

    [Fact]
    public void A_damaged_file_starts_over_and_is_replaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ broken");
        var store = new ProgressStore(File_);
        Assert.Null(store.Get("s", "g"));
        store.Set("s", "g", new ReadingProgress(1, 0, 2, false, At, "t"));
        Assert.Equal(1, new ProgressStore(File_).Get("s", "g")!.Page);
        Assert.False(File.Exists(File_ + ".tmp"));
    }
}
