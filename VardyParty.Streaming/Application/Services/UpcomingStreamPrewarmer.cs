using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Streaming;

/// <summary>
/// Keeps the next <see cref="PlaybackUrlReusePolicy.PrewarmDepth"/> one-shot V1 rows in
/// switch order warm while a stream plays, so Next plays an unburned URL without a
/// ~12s <c>/play</c>. At most one pre-resolve <c>/play</c> is in flight at a time.
/// </summary>
public interface IUpcomingStreamPrewarmer
{
    /// <summary>Starts the refresh loop for one playback session. Dispose to stop.</summary>
    IDisposable? Start(CancellationToken cancellationToken);

    /// <summary>Runs one refresh step now (after a switch) without waiting.</summary>
    void Kick();
}

public sealed class UpcomingStreamPrewarmer(
    IApiService apiService,
    IStreamSwitchingService switching,
    WarmPlaybackUrlCache cache,
    ILogger<UpcomingStreamPrewarmer> logger,
    TimeProvider? timeProvider = null) : IUpcomingStreamPrewarmer
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private int _busy;
    private CancellationTokenSource? _loopCts;

    public IDisposable? Start(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _loopCts, cts);
        CancelQuietly(previous);
        _ = LoopAsync(cts.Token);
        return new Stopper(this, cts);
    }

    public void Kick()
    {
        var token = Volatile.Read(ref _loopCts)?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
            return;
        _ = Task.Run(() => TickAsync(token), CancellationToken.None);
    }

    /// <summary>
    /// One step: pick the first candidate that needs a (re)resolve and run it, unless a
    /// pre-resolve is already running. Returns true when a <c>/play</c> was issued.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;

        EnrichedStream? target = null;
        try
        {
            var healthy = switching.GetHealthyStreams();
            cache.Prune(healthy.ToList());
            var upcoming = PlaybackUrlReusePolicy.UpcomingInSwitchOrder(
                healthy,
                switching.GetCurrentStream(),
                PlaybackUrlReusePolicy.PrewarmDepth);
            var now = _time.GetUtcNow();
            for (var depth = 0; depth < upcoming.Count && target == null; depth++)
            {
                var row = upcoming[depth];
                if (!PlaybackUrlReusePolicy.ShouldPrewarm(row) || cache.IsUnwarmable(row))
                    continue;
                var entry = cache.Peek(row);
                if (PlaybackUrlReusePolicy.NeedsPrewarmRefresh(entry?.Expiry, entry?.ResolvedAt, depth, now))
                    target = row;
            }

            if (target?.Stream is not { } stream || !cache.BeginInFlight(target))
                return false;

            try
            {
                logger.LogInformation("[Prewarm] Pre-resolving upcoming {Channel}", stream.Channel);
                var response = await apiService.GetM3U8UrlAsync(
                    stream.Url,
                    EnrichedStreamM3U8Apply.PlayerStreamNameForPlayback(stream),
                    stream.ResolutionStrategy,
                    stream.Source,
                    cancellationToken).ConfigureAwait(false);
                if (response is null || string.IsNullOrWhiteSpace(response.Url))
                {
                    logger.LogDebug("[Prewarm] No m3u8 for {Channel}", stream.Channel);
                    return true;
                }

                if (cache.Put(target, response, _time.GetUtcNow()))
                {
                    logger.LogInformation(
                        "[Prewarm] Warm m3u8 ready for {Channel} (expires in {Seconds}s)",
                        stream.Channel,
                        (int)(cache.Peek(target)!.Expiry - _time.GetUtcNow()).TotalSeconds);
                }
                else
                {
                    logger.LogInformation(
                        "[Prewarm] {Channel} token has no readable expiry - left on the fresh /play path",
                        stream.Channel);
                }

                return true;
            }
            finally
            {
                cache.EndInFlight(target);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Prewarm] Pre-resolve failed for {Channel}", target?.Stream?.Channel);
            return target != null;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TickInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Prewarm] Loop stopped");
        }
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        if (cts == null)
            return;
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private sealed class Stopper(UpcomingStreamPrewarmer owner, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref owner._loopCts, null, cts) == cts)
                CancelQuietly(cts);
            else
                try { cts.Cancel(); } catch { }
        }
    }
}
