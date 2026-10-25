using System.Collections.Concurrent;

namespace HentaiReader.Host.Engine;

/// <summary>
/// A dedicated thread that owns one Jint engine. Every engine access is a work item on this
/// thread; .NET completions (HTTP, storage, sleep) post their settlement here too.
/// </summary>
internal sealed class EngineThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public EngineThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public void Post(Action action)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Runs queued work on the engine thread until <paramref name="done"/> returns true, the
    /// deadline passes or the token is cancelled. Must be called on the engine thread.
    /// </summary>
    public void PumpUntil(Func<bool> done, DateTime deadline, CancellationToken token)
    {
        while (!done())
        {
            var wait = deadline - DateTime.UtcNow;
            if (wait <= TimeSpan.Zero) throw new TimeoutException("call exceeded the time limit");
            token.ThrowIfCancellationRequested();
            if (_queue.TryTake(out var action, (int)Min(wait, TimeSpan.FromMilliseconds(250)).TotalMilliseconds, token)) action();
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // A late completion of a call that already ended (timed out, engine replaced) has
                // nobody waiting for it; it must not take the whole app down.
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
