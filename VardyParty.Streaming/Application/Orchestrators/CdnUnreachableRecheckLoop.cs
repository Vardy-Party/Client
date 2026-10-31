using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Rechecks rows whose CDN could not be reached (<see cref="StreamDeferReason.CdnUnreachable"/>)
/// after a back-off: 2 min, then 5 min, then every 10 min, while the game is live
/// (<see cref="CdnReachabilityPolicy"/>). Such a row is never reported as failed and
/// never dropped. A recheck that comes back Healthy is accepted like any other row
/// (so it is appended to the pool and keeps stable numbering); one that is now dead
/// (404, decoy, DNS) is reported by the normal accept path; one still unreachable
/// waits for the next round.
/// <para>
/// The recheck runs alongside playback. The resolution session stays open until
/// the viewer leaves, the game stops being live, or no row is left to recheck.
/// </para>
/// </summary>
internal static class CdnUnreachableRecheckLoop
{
    /// <summary>
    /// Runs the rechecks and waits for them. Returns true when the viewer left the
    /// player meanwhile (the outcome is final, the caller returns at once).
    /// </summary>
    /// <param name="pending">The CDN-deferred rows. The accept callback adds rows that are still unreachable back to it.</param>
    /// <param name="resolve">Resolves and health-checks a batch of rows again.</param>
    /// <param name="accept">The orchestrator's normal accept path (report, hold healthy, flush to the pool).</param>
    /// <param name="currentPlayback">The playback task now running, if any.</param>
    /// <param name="viewerLeft">True once the viewer has left (the orchestrator's check).</param>
    /// <param name="keepRechecking">False once the game is no longer live.</param>
    /// <param name="delay">The back-off wait (a test seam).</param>
    /// <param name="onWaiting">Called before each wait with the row count and the wait.</param>
    public static async Task<bool> RunWhileSessionLiveAsync(
        List<StreamModel> pending,
        Func<List<StreamModel>, CancellationToken, IAsyncEnumerable<EnrichedStream>> resolve,
        Action<EnrichedStream> accept,
        Func<Task<PlaybackResult?>?> currentPlayback,
        Func<Task<bool>> viewerLeft,
        Func<bool> keepRechecking,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<int, TimeSpan> onWaiting,
        ILogger logger,
        CancellationToken sessionToken)
    {
        using var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        var rounds = new RoundSignal();
        var run = RunAsync(pending, resolve, accept, keepRechecking, delay, onWaiting, rounds, logger, recheckCts.Token);

        while (!run.IsCompleted)
        {
            var round = rounds.Next;
            var playing = currentPlayback();
            if (playing is { IsCompleted: false })
                await Task.WhenAny(run, round, playing).ConfigureAwait(false);
            else
                await Task.WhenAny(run, round).ConfigureAwait(false);

            if (await viewerLeft().ConfigureAwait(false))
            {
                recheckCts.Cancel();
                await run.ConfigureAwait(false);
                return true;
            }
        }

        await run.ConfigureAwait(false);
        return await viewerLeft().ConfigureAwait(false);
    }

    private static async Task RunAsync(
        List<StreamModel> pending,
        Func<List<StreamModel>, CancellationToken, IAsyncEnumerable<EnrichedStream>> resolve,
        Action<EnrichedStream> accept,
        Func<bool> keepRechecking,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<int, TimeSpan> onWaiting,
        RoundSignal rounds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Run off the caller's stack so the waiting loop starts at once.
        await Task.Yield();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                int count;
                lock (pending)
                    count = pending.Count;
                if (count == 0)
                    return;
                if (!keepRechecking())
                {
                    logger.LogInformation(
                        "[StreamResolution] {Count} CDN-unreachable stream(s) not rechecked: the game is no longer live (not reported, not dropped)",
                        count);
                    return;
                }

                var wait = CdnReachabilityPolicy.RecheckDelay(attempt);
                logger.LogInformation(
                    "[StreamResolution] {Count} stream(s) CDN unreachable; recheck {Attempt} in {Minutes} min (not reported, not dropped)",
                    count, attempt + 1, wait.TotalMinutes);
                onWaiting(count, wait);
                await delay(wait, cancellationToken).ConfigureAwait(false);
                if (!keepRechecking())
                    continue;

                List<StreamModel> rows;
                lock (pending)
                {
                    rows = pending.ToList();
                    pending.Clear();
                }

                try
                {
                    await foreach (var row in resolve(rows, cancellationToken).WithCancellation(cancellationToken))
                    {
                        // A row the local service could not answer this time is still
                        // waiting on the CDN recheck, not on the 5s/15s re-queue.
                        if (row.Status == StreamResolutionStatus.Deferred)
                            row.DeferReason = StreamDeferReason.CdnUnreachable;
                        accept(row);
                    }
                }
                finally
                {
                    rounds.Signal();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StreamResolution] CDN-unreachable recheck stopped");
        }
        finally
        {
            rounds.Signal();
        }
    }

    /// <summary>Completes once per finished recheck round, so the waiter can look at playback again.</summary>
    private sealed class RoundSignal
    {
        private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Next => Volatile.Read(ref _current).Task;

        public void Signal()
        {
            var done = Interlocked.Exchange(
                ref _current,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            done.TrySetResult();
        }
    }
}
