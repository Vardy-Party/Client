namespace VardyParty.Models;

/// <summary>
/// Stream-resolution rules shared by Streaming and Playback.
/// </summary>
public static class StreamCandidateRules
{
    /// <summary>Countdown pages are not playable candidates.</summary>
    public static bool ShouldSkipCountdown(bool isCountdown) => isCountdown;

    /// <summary>
    /// Cached M3U8 retry: only attach the fresh URL if it exists and differs (token/CDN rotation).
    /// </summary>
    public static bool ShouldAcceptFreshM3U8(string? failedCachedUrl, string? freshUrl)
        => !string.IsNullOrWhiteSpace(freshUrl)
           && !string.Equals(failedCachedUrl, freshUrl, StringComparison.OrdinalIgnoreCase);
}
