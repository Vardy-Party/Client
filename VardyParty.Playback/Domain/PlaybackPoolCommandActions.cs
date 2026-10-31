using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Playback;

public delegate Task<string?> ResolveFreshPlaybackUrlAsync(
    EnrichedStream stream,
    CancellationToken cancellationToken);

/// <summary>
/// Pool + fresh-resolve actions for every host (Android, Windows, Linux, iOS, MacCatalyst).
/// Lives in Playback so OS players do not each reimplement retry/accept. Playback does not
/// reference <c>IApiService</c>; hosts pass a resolve delegate.
/// Fresh URLs are accepted only via <see cref="PlaybackPolicy.ShouldAcceptFreshM3U8"/> against
/// <see cref="PlaybackSessionController.Snapshot"/> current URL — not a host-local field.
/// </summary>
public sealed class PlaybackPoolCommandActions(
    PlaybackSessionController session,
    IStreamSwitchingService switching,
    ResolveFreshPlaybackUrlAsync? resolveFresh,
    Action<string, bool, bool> attachViaSession,
    Action<PlaybackCommand> applyCommand)
{
    // Every host builds one of these per session. Wiring the live pool count here
    // means no host can hand the controller a stale count again (Android synced
    // only on user navigation, and an established error closed the whole session).
    private readonly bool _healthyCountWired = WireHealthyCount(session, switching);

    /// <summary>Test seam: delay before a same-stream fresh /play retry.</summary>
    internal Func<TimeSpan, Task> DelayAsync { get; set; } = static d => d > TimeSpan.Zero ? Task.Delay(d) : Task.CompletedTask;

    private static bool WireHealthyCount(PlaybackSessionController session, IStreamSwitchingService switching)
    {
        session.HealthyStreamCountProvider = () => switching.GetHealthyStreams().Count;
        return true;
    }

    public void ClearCurrentResolvedUrl()
    {
        var failed = switching.GetCurrentStream();
        if (failed != null)
            failed.ResolvedM3U8Url = null;
    }

    public void RemoveCurrentFromPool() => switching.RemoveCurrentStream();

    public void SyncHealthyStreamCount()
        => session.SetHealthyStreamCount(switching.GetHealthyStreams().Count);

    public void SwitchPoolToPrevious() => switching.SwitchToPreviousStream();

    public async Task AttachCurrentFromPoolAsync()
    {
        var current = switching.GetCurrentStream();
        if (current == null)
            return;

        // FB/V1 soak tokens are one-shot. Find/prefetch ResolvedM3U8Url is often
        // already burned — always /play again when a resolver is wired. Fall back
        // to the cached URL only when fresh resolve fails (and mark usedCachedUrl
        // so ShouldRetryFreshResolve can still fire once).
        // Expiry-aware reuse: a readable token with comfortable life left (and not a
        // known single-use source) plays now; a failed start still re-/plays once.
        if (resolveFresh != null
            && PlaybackUrlReusePolicy.CanReuseCached(current, DateTimeOffset.UtcNow))
        {
            attachViaSession(current.ResolvedM3U8Url!, true, true);
            return;
        }

        string? url = null;
        var usedCachedUrl = false;
        if (current.Stream != null && resolveFresh != null)
        {
            try
            {
                url = await resolveFresh(current, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(url))
                    current.ResolvedM3U8Url = url;
            }
            catch
            {
                // Fall through to cached URL below.
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            url = current.ResolvedM3U8Url;
            usedCachedUrl = !string.IsNullOrWhiteSpace(url);
        }

        if (string.IsNullOrWhiteSpace(url))
            return;

        attachViaSession(url, usedCachedUrl, true);
    }

    /// <summary>
    /// Make-before-break index-driven switch (Next / Previous / chip) onto the pool's
    /// current row. Reusable URLs (<see cref="PlaybackUrlReusePolicy.PlanSwitch"/>) attach
    /// straight away with one fresh retry on a failed start. Otherwise the stream that is
    /// playing keeps playing while <c>/play</c> (or a warm pre-resolve) runs, with
    /// <paramref name="showStatus"/> told "Switching to X..."; on failure the pool index is
    /// put back on the playing row (without re-attaching it) and the error is shown.
    /// </summary>
    /// <returns>True when a new source was handed to the player.</returns>
    public async Task<bool> SwitchToCurrentAsync(
        bool force,
        Action<string>? showStatus,
        Action<Action> runWithIndexSwitchSuppressed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runWithIndexSwitchSuppressed);
        var target = switching.GetCurrentStream();
        var playingUrl = session.Snapshot.CurrentUrl;
        var plan = PlaybackUrlReusePolicy.PlanSwitch(target, DateTimeOffset.UtcNow);
        if (plan.Kind == PlaybackSwitchKind.None || target is null)
            return false;

        // An index-driven switch (Next, Prev, chip) replaces any same-stream retry.
        session.CancelSameStreamRecovery();

        if (plan.Kind == PlaybackSwitchKind.AttachCached || resolveFresh is null)
        {
            var cached = plan.Url ?? target.ResolvedM3U8Url;
            if (string.IsNullOrWhiteSpace(cached)
                || (!force && string.Equals(playingUrl, cached, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            attachViaSession(cached, true, true);
            return true;
        }

        var playing = FindPlayingRow(playingUrl, target);
        var channel = target.Stream?.Channel;
        showStatus?.Invoke(PlaybackUrlReusePolicy.SwitchPendingText(channel));

        string? fresh = null;
        try
        {
            fresh = await resolveFresh(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            // Treated as a failed switch below.
        }

        // A newer switch owns the pool now.
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(switching.GetCurrentStream(), target))
            return false;

        if (string.IsNullOrWhiteSpace(fresh))
        {
            showStatus?.Invoke(PlaybackUrlReusePolicy.SwitchFailedText(channel));
            if (playing != null)
                RevertIndexTo(playing, runWithIndexSwitchSuppressed);
            else if (string.IsNullOrWhiteSpace(playingUrl) && !string.IsNullOrWhiteSpace(target.ResolvedM3U8Url))
                attachViaSession(target.ResolvedM3U8Url, true, true);
            return false;
        }

        if (!force && string.Equals(playingUrl, fresh, StringComparison.OrdinalIgnoreCase))
            return false;

        attachViaSession(fresh, false, true);
        return true;
    }

    /// <summary>Pool row whose resolved URL is the one the session is playing.</summary>
    internal EnrichedStream? FindPlayingRow(string? playingUrl, EnrichedStream? exclude)
    {
        if (string.IsNullOrWhiteSpace(playingUrl))
            return null;
        foreach (var row in switching.GetHealthyStreams())
        {
            if (!ReferenceEquals(row, exclude)
                && string.Equals(row.ResolvedM3U8Url, playingUrl, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Revert after a failed switch: move the pool index to the row whose resolved URL
    /// is <paramref name="playingUrl"/> (the last-good stream). No-op when no row
    /// matches or it is already current. Callers hold index-switch suppression.
    /// </summary>
    /// <returns>True when the index moved.</returns>
    public bool RevertIndexToPlayingUrl(string? playingUrl)
    {
        var row = FindPlayingRow(playingUrl, exclude: null);
        if (row is null || ReferenceEquals(row, switching.GetCurrentStream()))
            return false;

        var healthy = switching.GetHealthyStreams();
        for (var i = 0; i < healthy.Count; i++)
        {
            if (ReferenceEquals(healthy[i], row))
                return switching.SwitchToStream(i);
        }

        return false;
    }

    private void RevertIndexTo(EnrichedStream playing, Action<Action> runWithIndexSwitchSuppressed)
    {
        var healthy = switching.GetHealthyStreams();
        for (var i = 0; i < healthy.Count; i++)
        {
            if (ReferenceEquals(healthy[i], playing))
            {
                var index = i;
                // Suppressed: the row is already playing - re-attaching it would re-/play
                // the stream being left.
                runWithIndexSwitchSuppressed(() => switching.SwitchToStream(index));
                return;
            }
        }
    }

    public async Task RetryFreshResolveAsync()
    {
        try
        {
            var current = switching.GetCurrentStream();
            var snapshot = session.Snapshot;
            var recovering = snapshot.IsRecovering;
            if (recovering)
            {
                // Same-stream retry: back off before the next /play (0s, 1s, 3s).
                await DelayAsync(PlaybackPolicy.SameStreamRetryBackoff(snapshot.SameStreamRetries)).ConfigureAwait(false);

                // The user moved on while we waited, so this retry is stale.
                if (!session.Snapshot.IsRecovering || !ReferenceEquals(switching.GetCurrentStream(), current))
                    return;
            }

            string? fresh = null;
            if (current != null && resolveFresh != null)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        fresh = await resolveFresh(current, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    catch (InfrastructureUnavailableException ex)
                        when (InfrastructureRetryPolicy.ShouldRetryFreshResolve(ex, attempt))
                    {
                        // Service blip, not the stream: wait and ask again.
                        await DelayAsync(InfrastructureRetryPolicy.FreshResolveDelays[attempt]).ConfigureAwait(false);
                        if (IsStale(recovering, current))
                            return;
                    }
                }
            }
            if (recovering && !session.Snapshot.IsRecovering)
                return;

            // During a same-stream retry, the same URL counts too. Reattaching
            // reloads the playlist, and some CDNs re-sign a stable URL server-side.
            var accept = recovering
                ? !string.IsNullOrWhiteSpace(fresh)
                : !string.IsNullOrWhiteSpace(fresh)
                  && PlaybackPolicy.ShouldAcceptFreshM3U8(session.Snapshot.CurrentUrl, fresh);
            if (!accept)
            {
                applyCommand(PlaybackCommand.FromEffects(session.NotifyFreshResolveUnavailable()));
                return;
            }

            if (current != null)
                current.ResolvedM3U8Url = fresh;

            attachViaSession(fresh!, false, true);
        }
        catch (InfrastructureUnavailableException ex)
        {
            // Retries exhausted (or MP disabled / auth): defer, never report or drop.
            applyCommand(PlaybackCommand.FromEffects(session.NotifyFreshResolveInfrastructureUnavailable(ex.Message)));
        }
        catch (Exception ex)
        {
            applyCommand(PlaybackCommand.FromEffects(session.NotifyFreshResolveUnavailable(ex.Message)));
        }
    }

    /// <summary>
    /// The user moved to another row (or the session closed) while an
    /// infrastructure retry waited.
    /// </summary>
    private bool IsStale(bool recovering, EnrichedStream current)
        => session.Snapshot.State == PlaybackSessionState.Closed
           || (recovering && !session.Snapshot.IsRecovering)
           || !ReferenceEquals(switching.GetCurrentStream(), current);
}
