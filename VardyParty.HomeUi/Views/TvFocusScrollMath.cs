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
    /// Layout padding (whole dp, so layout slots stay crisp) a strip needs
    /// on ONE side for the focused card's chrome to render un-clipped at
    /// rest: the chrome overhead rounded up. Ancestor clipping is disabled
    /// on Android (TvDpadFocusRouter.HardenContainers), but chrome can only
    /// DRAW into space that exists — at the screen edge or against opaque
    /// siblings the strip itself must reserve the room. Horizontally this is
    /// the strip's start/end padding (ClipToPadding stays off natively so
    /// cards still scroll edge-to-edge); vertically it is the headroom
    /// HomeLayoutState.RowHeight adds around the card. Single source of
    /// truth with the scroll-target math above: the padding is derived from
    /// the SAME overhead the chrome actually renders, never a separate
    /// magic number.
    /// </summary>
    public static double FocusChromePadding(double cardDimension, double ringThickness) =>
        Math.Ceiling(FocusChromeOverhead(cardDimension, ringThickness));

    /// <summary>
    /// Target ScrollX that keeps the card's layout rect PLUS its focus-chrome
    /// overhead fully inside the strip viewport, or null when no scroll is
    /// needed. MakeVisible aligned the layout rect only, so a card revealed
    /// "exactly" still clipped its +9% scale and ring at the viewport edge.
    /// Clamped to the scrollable range [0, extent]: at the content ends the
    /// strip's own chrome-derived end padding (<see cref="FocusChromePadding"/>)
    /// is all the headroom that physically exists. A target within
    /// <see cref="ChromeComfortPad"/> of an edge
    /// snaps TO that edge: the end card sits inside the strip's end padding,
    /// so the residual is the padding-minus-overhead sliver (sub-dp, the
    /// ceiling remainder) — resting the strip exactly at its boundary shows strictly
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
    /// Vertical scroll delta (native px) that parks the focused row's TOP —
    /// league header first — at the top of the rows viewport, Netflix-style.
    /// The earlier minimal reveal (bottom-edge alignment on downward moves)
    /// left rows resting part-clipped in every focus path that was not a
    /// router vertical move; the field report was focused cards "not fully on
    /// screen". Top alignment is deterministic: the focused row always rests
    /// at the same place, its header and the strip's chrome headroom fully
    /// visible, rows above completely scrolled off. Positive scrolls down,
    /// negative up, matching RecyclerView.smoothScrollBy — which clamps at
    /// the content edges, so the last rows simply rest against the bottom
    /// (fully visible; a row can never park clipped).
    /// </summary>
    public static int ComputeRowTopAlignDelta(int rowTop, int viewportHeight) =>
        viewportHeight <= 0 ? 0 : rowTop;
}
