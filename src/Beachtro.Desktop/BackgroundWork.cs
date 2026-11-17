using Beachtro.Application.Security;
using Beachtro.Application.Sync;

namespace Beachtro.Desktop;

/// <summary>
/// Background loop (sync design §Scheduling): shortly after start, then about once a minute, runs the
/// scheduled jobs as the signed-in user and one bounded sync cycle. Never blocks the UI or checkout;
/// errors are logged and the next tick tries again. Stopping cancels network work; restart state comes
/// from the database (leases and due keys), not from memory.
/// </summary>
public sealed class BackgroundWork(ISyncService sync, IJobScheduler scheduler, SessionManager sessions) : IDisposable
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Asks for a cycle soon (for example right after sign-in), without waiting for the minute.</summary>
    public void Nudge()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already nudged.
            }
        }
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Leases expire on their own; nothing is lost by stopping mid-cycle.
            }
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(FirstDelay, stop).ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var messages = await scheduler.RunDueAsync(sessions.Current, stop).ConfigureAwait(false);
                    foreach (var m in messages)
                    {
                        Serilog.Log.Information("Scheduled job: {Message}", m);
                    }

                    var cycle = await sync.RunCycleAsync(stop).ConfigureAwait(false);
                    if (cycle != SyncCycleResult.Empty)
                    {
                        Serilog.Log.Information("Sync cycle: {@Cycle}", cycle);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Serilog.Log.Error(ex, "Background work cycle failed; it will run again");
                }

                await _wake.WaitAsync(Interval, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        _stop.Dispose();
        _wake.Dispose();
    }
}
