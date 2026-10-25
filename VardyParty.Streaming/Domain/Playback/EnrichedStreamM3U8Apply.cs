using System.Linq;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// Applies a fresh <see cref="M3U8Response"/> onto a pool row so ExoPlayer gets
/// the new URL together with matching Referer / Cookie / rewritten segments.
/// </summary>
public static class EnrichedStreamM3U8Apply
{
    public static void Apply(EnrichedStream current, M3U8Response response)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(response);

        current.ResolvedM3U8Url = response.Url;
        current.RequestHeaders = response.RequestHeaders;
        current.RewrittenSegments = response.RewrittenSegments?
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToList();

        if (response.RequestHeaders is not { Count: > 0 })
            return;

        foreach (var pair in response.RequestHeaders)
        {
            if (pair.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                current.Referer = pair.Value;
                return;
            }
        }
    }

    /// <summary>
    /// Player-stream chip name for V2 /play, matching <see cref="ApiService"/> playback resolve.
    /// </summary>
    public static string? PlayerStreamNameForPlayback(Kernel.Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.RequiresV2StreamSelection
            && !stream.IsCountdown
            && !string.IsNullOrWhiteSpace(stream.PlayerStream)
                ? stream.PlayerStream.Trim()
                : null;
    }
}
