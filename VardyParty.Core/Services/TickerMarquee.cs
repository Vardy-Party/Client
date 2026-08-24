namespace VardyParty.Services;

/// <summary>
/// Shared marquee wrap math. Platforms only apply TranslationX.
/// Offset is the signed X translation (negative = content moved left).
/// </summary>
public static class TickerMarquee
{
    public static bool ShouldLoop(double contentWidth, double viewportWidth) =>
        viewportWidth > 0 && contentWidth > viewportWidth;

    public static double LoopPeriod(double contentWidth, double gapWidth)
    {
        var content = Math.Max(0, contentWidth);
        var gap = Math.Max(0, gapWidth);
        return content + gap;
    }

    /// <summary>
    /// Maps any offset into (-loopPeriod, 0] so copy B sits where copy A started.
    /// </summary>
    public static double Wrap(double offset, double loopPeriod)
    {
        if (loopPeriod <= 0)
        {
            return 0;
        }

        var wrapped = offset % loopPeriod;
        if (wrapped > 0)
        {
            wrapped -= loopPeriod;
        }
        else if (wrapped <= -loopPeriod)
        {
            wrapped += loopPeriod;
        }

        return wrapped;
    }

    public static double AdvanceLeft(double offset, double pixels, double loopPeriod) =>
        Wrap(offset - Math.Max(0, pixels), loopPeriod);

    /// <summary>
    /// Distance scrolled left, kept in [0, loopPeriod).
    /// </summary>
    public static double WrapPositive(double distance, double loopPeriod)
    {
        if (loopPeriod <= 0)
        {
            return 0;
        }

        var wrapped = distance % loopPeriod;
        if (wrapped < 0)
        {
            wrapped += loopPeriod;
        }

        return wrapped;
    }
}
