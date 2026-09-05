namespace VardyParty.Linux.Services;

/// <summary>
/// Computes the screen rectangle the Avalonia chrome overlay should cover —
/// the video row only, leaving the reserved Close / match-toast strip alone.
/// </summary>
public static class LinuxPlaybackChromePlacement
{
    public static bool TryComputeVideoRowBounds(
        double hostScreenX,
        double hostScreenY,
        double hostWidth,
        double hostHeight,
        double chromeRowHeight,
        out double x,
        out double y,
        out double width,
        out double height)
    {
        x = hostScreenX;
        y = hostScreenY + Math.Max(0, chromeRowHeight);
        width = hostWidth;
        height = hostHeight - Math.Max(0, chromeRowHeight);

        if (width <= 1 || height <= 1)
        {
            x = y = width = height = 0;
            return false;
        }

        return true;
    }
}
