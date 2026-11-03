using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Streaming;

namespace VardyParty.Hosting;

/// <summary>
/// Adapts <see cref="IApiService"/> onto Playback's resolve delegate so OS players
/// never take a catalog HTTP client. Warm take order is always TryTake before
/// WaitForInFlight (all platforms; <see cref="FirstPlayOpenStrategy"/>).
/// </summary>
public static class PlaybackUrlResolver
{
    public static ResolveFreshPlaybackUrlAsync Bind(
        IApiService api,
        WarmPlaybackUrlCache? warm = null,
        TimeProvider? timeProvider = null,
        ResolverRaceThrottle? raceThrottle = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        var time = timeProvider ?? TimeProvider.System;
        return ResolveAsync;

        async Task<string?> ResolveAsync(EnrichedStream current, CancellationToken cancellationToken)
        {
            if (current.Stream == null)
                return null;

            // FirstPlayOpenStrategy: TryTake before WaitForInFlight so a partner/race
            // warm is not blocked by a redundant Kick /play.
            if (warm != null)
            {
                if (warm.TryTake(current, time.GetUtcNow(), out var immediate))
                {
                    EnrichedStreamM3U8Apply.Apply(current, immediate);
                    return immediate.Url;
                }

                await warm.WaitForInFlightAsync(current, cancellationToken).ConfigureAwait(false);
                if (warm.TryTake(current, time.GetUtcNow(), out var afterWait))
                {
                    EnrichedStreamM3U8Apply.Apply(current, afterWait);
                    return afterWait.Url;
                }
            }

            // Next / switch fresh resolves share the playback-priority throttle so
            // find-phase harvest yields Chromium slots to the URL we need to attach.
            using var raceHold = raceThrottle?.BeginRace();
            using var raceLeg = raceThrottle?.MarkRaceLeg();

            // FB /play tokens are one-shot — always go through GetM3U8UrlAsync and
            // refresh Referer/headers on the pool row for the new URL.
            var response = await api.GetM3U8UrlAsync(
                current.Stream.Url,
                EnrichedStreamM3U8Apply.PlayerStreamNameForPlayback(current.Stream),
                current.Stream.ResolutionStrategy,
                current.Stream.Source,
                cancellationToken);

            if (response is null || string.IsNullOrWhiteSpace(response.Url))
                return null;

            EnrichedStreamM3U8Apply.Apply(current, response);
            return response.Url;
        }
    }
}
