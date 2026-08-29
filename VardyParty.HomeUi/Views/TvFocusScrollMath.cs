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
    /// Focused-card scale. Kept at 1.0 so the focus ring can sit on the card
    /// edge (half-stroke into the strip comfort pad) without a +9% transform
    /// overflowing the content-height league row (<c>LeagueRowHeight</c>).
    /// </summary>
    public const double FocusScale = 1.0;

    /// <summary>
    /// Breathing room beyond the card layout rect so a focused card's edge
    /// ring (half the stroke paints outside) is not flush against / sheared
    /// by the strip viewport. Must be >= half of the thickest focus ring
    /// (TV uses 5dp → need ≥3; 4 covers it with a little air).
    /// </summary>
    public const double ChromeComfortPad = 4;

    /// <summary>
    /// How far focus chrome needs beyond ONE side of the card's layout rect.
    /// FocusScale is 1.0; the ring sits on the card edge so half its stroke
    /// paints outside, plus <see cref="ChromeComfortPad"/>.
    /// </summary>
    public static double FocusChromeOverhead(double cardDimension, double ringThickness)
    {
        _ = cardDimension;
        return (ringThickness * FocusScale / 2) + ChromeComfortPad;
    }

    /// <summary>
    /// Layout padding (whole dp) a strip needs on ONE side so a focused card
    /// is not flush against the viewport edge. Ancestor clipping is still
    /// disabled on Android for safety, but the selection ring no longer
    /// depends on drawing outside the card.
    /// </summary>
    public static double FocusChromePadding(double cardDimension, double ringThickness) =>
        Math.Ceiling(FocusChromeOverhead(cardDimension, ringThickness));

    /// <summary>
    /// Target ScrollX that keeps the card's layout rect PLUS its focus-chrome
    /// overhead fully inside the strip viewport, or null when no scroll is
    /// needed. Clamped to the scrollable range [0, extent]. A target within
    /// <see cref="ChromeComfortPad"/> of an edge snaps TO that edge.
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
    /// Vertical scroll delta (native px) that parks the focused row's TOP —
    /// league header first — at the top of the rows viewport, Netflix-style.
    /// Positive scrolls down, negative up, matching RecyclerView.smoothScrollBy.
    /// </summary>
    public static int ComputeRowTopAlignDelta(int rowTop, int viewportHeight) =>
        viewportHeight <= 0 ? 0 : rowTop;

    /// <summary>
    /// Whether a focus landing should ScrollTo the row. The first board row
    /// on TV is already at document start — ScrollTo(Start) of that row
    /// shoved it off the top (Serie A field report).
    /// </summary>
    public static bool ShouldScrollRowIntoView(bool isTv, System.Collections.IList? items, object row)
    {
        if (!isTv || items is null || items.Count == 0)
        {
            return !isTv;
        }

        return !ReferenceEquals(items[0], row);
    }
}
