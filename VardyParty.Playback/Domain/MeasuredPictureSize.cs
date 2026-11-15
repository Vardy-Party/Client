using VardyParty.Kernel;

namespace VardyParty.Playback;

/// <summary>
/// Keeps a stream's stored picture size in step with what the player measures.
/// The health check stores a bitstream-probed size per stream, but a live stream
/// can change size mid-play. Once <see cref="DecodedPictureSizeSettler"/> publishes
/// a new settled size, every host (Android, Windows, Linux) calls
/// <see cref="Remember"/> so the overlay, the stream toast and switch-back read the
/// new height instead of the stale stored one.
/// </summary>
public static class MeasuredPictureSize
{
    /// <summary>
    /// Copies <paramref name="settled"/> into <paramref name="health"/>'s probed size.
    /// Returns true when the stored size changed.
    /// </summary>
    public static bool Remember(StreamHealth? health, (int Width, int Height)? settled)
    {
        if (health is null || settled is not { Width: > 0, Height: > 0 } size)
            return false;
        if (health.ProbedVideoWidth == size.Width && health.ProbedVideoHeight == size.Height)
            return false;

        health.ProbedVideoWidth = size.Width;
        health.ProbedVideoHeight = size.Height;
        return true;
    }
}
