namespace VardyParty.HomeUi.Views;

/// <summary>
/// Pure geometry for the TV focus scroll ownership (no platform types, unit
/// tested headlessly). TvDpadFocusRouter (Android) uses it to compute the
/// vertical recycler reveal it performs itself BEFORE moving focus, so the
/// framework's own duplicate reveal stays suppressed and exactly one
/// animated scroll owns each axis.
/// </summary>
public static class TvFocusScrollMath
{
    /// <summary>
    /// Vertical scroll delta (native px) that reveals the whole row item —
    /// league header, strip and the strip's built-in chrome headroom — in the
    /// rows viewport. 0 means already fully visible; a row taller than the
    /// viewport aligns its top. Positive scrolls down, negative up, matching
    /// RecyclerView.smoothScrollBy.
    /// </summary>
    public static int ComputeVerticalRevealDelta(int rowTop, int rowBottom, int viewportHeight)
    {
        if (viewportHeight <= 0)
        {
            return 0;
        }

        if (rowBottom - rowTop >= viewportHeight || rowTop < 0)
        {
            return rowTop;
        }

        return rowBottom > viewportHeight ? rowBottom - viewportHeight : 0;
    }
}
