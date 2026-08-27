namespace VardyParty.HomeUi.Views;

/// <summary>
/// Pure geometry for the TV focus scroll ownership (no platform types, unit
/// tested headlessly). <see cref="MatchCardView"/> uses it to compute
/// chrome-inflated horizontal strip targets; TvDpadFocusRouter (Android)
/// uses it to compute the vertical recycler reveal it performs itself
/// BEFORE moving focus, so the framework's own duplicate reveal stays
/// suppressed and exactly one animated scroll owns each axis.
/// </summary>
public static class TvFocusScrollMath
{
    /// <summary>
    /// The focused card's scale bump. Single source of truth shared with the
    /// card chrome animations: the scroll math must inflate by the SAME
    /// overflow the chrome actually renders.
    /// </summary>
    public const double FocusScale = 1.09;

    /// <summary>
    /// Breathing room beyond the exact chrome edge so the ring never sits
    /// flush against the viewport boundary.
    /// </summary>
    public const double ChromeComfortPad = 4;

    /// <summary>
    /// How far the focus chrome renders beyond ONE side of the card's layout
    /// rect: the scale overflow at the current card size plus the focus ring
    /// (which scales with the card), plus a small comfort pad. At the TV
    /// metrics (300dp card, 5dp ring) this is ~23dp per side — the "~24px"
    /// the field report estimated from the clipped ring.
    /// </summary>
    public static double FocusChromeOverhead(double cardDimension, double ringThickness) =>
        ((FocusScale - 1) / 2 * cardDimension) + (ringThickness * FocusScale) + ChromeComfortPad;

    /// <summary>
    /// Target ScrollX that keeps the card's layout rect PLUS its focus-chrome
    /// overhead fully inside the strip viewport, or null when no scroll is
    /// needed. MakeVisible aligned the layout rect only, so a card revealed
    /// "exactly" still clipped its +9% scale and ring at the viewport edge.
    /// Clamped to the scrollable range [0, extent]: at the content ends the
    /// strip's own 24dp end padding is all the headroom that physically
    /// exists. A target within <see cref="ChromeComfortPad"/> of an edge
    /// snaps TO that edge: the end card sits inside the strip's end padding,
    /// so the residual is the padding-minus-overhead sliver (1dp at the TV
    /// metrics) — resting the strip exactly at its boundary shows strictly
    /// MORE chrome headroom than the sliver-offset target and never leaves
    /// the strip parked one pixel off its natural end state.
    /// </summary>
    public static double? ComputeStripTarget(
        double cardLeft,
        double cardWidth,
        double overhead,
        double viewportWidth,
        double contentWidth,
        double currentScrollX)
    {
        if (cardWidth <= 0 || viewportWidth <= 0)
        {
            return null;
        }

        var wantedLeft = cardLeft - overhead;
        var wantedRight = cardLeft + cardWidth + overhead;
        var maxScroll = Math.Max(0, contentWidth - viewportWidth);

        double target;
        if (wantedLeft < currentScrollX)
        {
            target = wantedLeft;
        }
        else if (wantedRight > currentScrollX + viewportWidth)
        {
            target = wantedRight - viewportWidth;
        }
        else
        {
            return null;
        }

        target = Math.Clamp(target, 0, maxScroll);

        if (target <= ChromeComfortPad)
        {
            return 0;
        }

        if (maxScroll - target <= ChromeComfortPad)
        {
            return maxScroll;
        }

        return target;
    }

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
