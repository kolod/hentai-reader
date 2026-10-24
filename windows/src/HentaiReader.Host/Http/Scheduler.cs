namespace HentaiReader.Host.Http;

public sealed record Politeness(int MinIntervalMs, int MaxConcurrency);

/// <summary>
/// Enforces a minimum interval between request starts and a maximum number of requests in flight,
/// first come first served (port of the runner's Scheduler). Shared by script and image requests.
/// </summary>
public sealed class Scheduler
{
    private readonly Func<Politeness?> _limits;
    private readonly SemaphoreSlim _order = new(1, 1);
    private readonly object _lock = new();
    private int _inFlight;
    private long _lastStart = long.MinValue / 2;
    private TaskCompletionSource _released = NewSignal();

    /// <param name="limits">Current limits, or null to disable (offline replay).</param>
    public Scheduler(Func<Politeness?> limits) => _limits = limits;

    public async Task AcquireAsync(CancellationToken token = default)
    {
        await _order.WaitAsync(token);
        try
        {
            var lim = _limits();
            if (lim is not null)
            {
                while (true)
                {
                    Task wait;
                    lock (_lock)
                    {
                        if (_inFlight < lim.MaxConcurrency) break;
                        wait = _released.Task;
                    }
                    await wait.WaitAsync(token);
                }
                // Task.Delay may wake slightly early on the coarse Windows timer, so re-check.
                long delay;
                while ((delay = _lastStart + lim.MinIntervalMs - NowMs()) > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
            }
            lock (_lock)
            {
                _lastStart = NowMs();
                _inFlight++;
            }
        }
        finally
        {
            _order.Release();
        }
    }

    public void Release()
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            _inFlight--;
            signal = _released;
            _released = NewSignal();
        }
        signal.TrySetResult();
    }

    // Environment.TickCount64 ticks in ~15.6 ms steps on Windows, too coarse for the interval.
    private static long NowMs() => (long)System.Diagnostics.Stopwatch.GetElapsedTime(0).TotalMilliseconds;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
