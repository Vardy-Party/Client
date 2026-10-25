using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// When LocalService already soaked a playlist (V1 cookies) or rewritten media
/// (V2), a Client CDN probe can still fail on TV DNS / geo while ExoPlayer with
/// the same LS headers would play. Prefer trusting that soak over discarding.
/// </summary>
internal static class LocalServiceSoakTrust
{
    public static bool ShouldTrust(M3U8Response? response, StreamHealthStatus failedStatus)
    {
        if (response is null || failedStatus == StreamHealthStatus.Healthy)
            return false;

        if (failedStatus is not (StreamHealthStatus.ManifestUnreachable
            or StreamHealthStatus.SegmentUnreachable))
        {
            return false;
        }

        if (MpRewrittenSegment.HasProbeReady(response.RewrittenSegments))
            return true;

        return HasCapturedCookie(response.RequestHeaders);
    }

    private static bool HasCapturedCookie(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return false;

        foreach (var pair in headers)
        {
            if (pair.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return true;
            }
        }

        return false;
    }
}
