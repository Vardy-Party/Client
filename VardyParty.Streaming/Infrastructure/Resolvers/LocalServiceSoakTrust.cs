using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// When LocalService already soaked a playlist (V1 cookies / Referer) or
/// rewritten media (V2), skip Client CDN probing and trust that soak.
/// Signed m3u8 tokens are often one-shot: a health GET burns the URL before
/// ExoPlayer can play. Playback must <c>/play</c> again for every attach.
/// </summary>
public static class LocalServiceSoakTrust
{
    /// <summary>
    /// LS left playback soak — do not HIT the CDN again from the Client.
    /// </summary>
    public static bool ShouldSkipClientCdnProbe(M3U8Response? response) =>
        HasSoakEvidence(response);

    /// <summary>
    /// Pool row already carries soak headers / rewritten samples from find —
    /// treat as one-shot FB/V1 (or V2) and re-resolve before ExoPlayer.
    /// </summary>
    public static bool RequiresFreshM3u8ForPlayback(EnrichedStream? stream)
    {
        if (stream is null)
            return false;

        if (MpRewrittenSegment.HasProbeReady(stream.RewrittenSegments))
            return true;

        return HasPlaybackSoakHeaders(stream.RequestHeaders);
    }

    /// <summary>
    /// Soak evidence that would have justified trust after a failed CDN probe.
    /// Callers must skip the probe via <see cref="ShouldSkipClientCdnProbe"/> instead —
    /// probe-then-trust is unreachable when skip uses the same evidence.
    /// </summary>
    public static bool ShouldTrust(M3U8Response? response, StreamHealthStatus failedStatus)
    {
        if (response is null || failedStatus == StreamHealthStatus.Healthy)
            return false;

        if (failedStatus is not (StreamHealthStatus.ManifestUnreachable
            or StreamHealthStatus.SegmentUnreachable))
        {
            return false;
        }

        return HasSoakEvidence(response);
    }

    private static bool HasSoakEvidence(M3U8Response? response)
    {
        if (response is null)
            return false;

        if (MpRewrittenSegment.HasProbeReady(response.RewrittenSegments))
            return true;

        // V1/FB /play often returns Referer/Origin without Cookie; ExoPlayer still
        // plays with those. Cookie alone was too strict — TV CDN probes fail and
        // healthy FB rows get discarded.
        return HasPlaybackSoakHeaders(response.RequestHeaders);
    }

    private static bool HasPlaybackSoakHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return false;

        var hasRefererOrOrigin = false;
        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;

            if (pair.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                return true;

            if (pair.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("origin", StringComparison.OrdinalIgnoreCase))
            {
                hasRefererOrOrigin = true;
            }
        }

        return hasRefererOrOrigin;
    }
}
