namespace VardyParty.Presentation;

/// <summary>
/// Concrete sizing for one <see cref="HomeLayoutClass"/>. All values are
/// device-independent pixels so the same numbers work on every renderer.
/// </summary>
public sealed record HomeLayoutMetrics(
    double CardWidth,
    double CardHeight,
    double CardCornerRadius,
    double BadgeSize,
    double TeamFontSize,
    double ScoreFontSize,
    double StatusFontSize,
    double AggregateFontSize,
    double LeagueTitleFontSize,
    double LeagueIconSize,
    double PageTitleFontSize,
    double PageSubtitleFontSize,
    double PagePadding,
    double RowSpacing,
    double CardSpacing,
    double BrandLogoSize,
    bool FlatCardChrome = false,
    int StagedStripCards = 0,
    double FocusRingThickness = 3,
    double FocusedCardLift = 0)
{
    public static HomeLayoutMetrics For(HomeLayoutClass layoutClass) => layoutClass switch
    {
        // 10-foot UI: readable from the sofa without dominating the panel.
        // Field-tested down THREE times from 440x232 (user: "too big", the
        // last verdict on 340x180). 300x160 fits ~5.8 cards per row and ~3.9
        // league rows on 1080p; type/badge sizes hold the revised 10-foot
        // floors (status chip >= 15, score >= 30, badge >= 50) and still fit
        // the card box (inner 272x138: status row + badge + two name lines).
        // RowSpacing is the inter-league gap, applied ABOVE each league header
        // (HomeLayoutState.RowMarginThickness) so a header binds visually to
        // its own card row. Field-tested up on Desktop (28->40: sections ran
        // together). TV got only modest breathing room (28->32): TV rows were
        // deliberately tightened to fit ~3.5 rows on a 1080p panel and the
        // Metrics_TvCardsFitAGridOnA1080pPanel test still holds at 32.
        //
        // LeagueIconSize: field-tested up from 30/26/22/20 — the league mark
        // next to the header title read as a barely-legible dot on a desktop
        // window. It now sits clearly above the title's line height
        // (>= ~1.6x LeagueTitleFontSize) so it reads as a proper crest.
        // FlatCardChrome: the TV class drops the per-card composition Shadows
        // (card drop shadow + four badge shadows) and simplifies the team
        // wash — ~37 shadow blurs + diagonal 4-stop gradients were a large
        // slice of the 1.3s full-tree pass on the 32-bit box, and a flat card
        // with a subtle border reads fine at 10 feet. Desktop/phone keep the
        // full treatment (GPU headroom).
        // StagedStripCards: the card strips are BindableLayouts (PR #76's
        // WinUI-crash constraint forbids nested CollectionViews), so a strip
        // materializes EVERY card in the row when it binds — a 15-card cup
        // row is a >500ms single layout pass on the weak core. On TV a new
        // row materializes its first 8 cards (~5.5 visible + headroom) and
        // the rest arrive in dispatcher-idle chunks. Rows themselves are
        // already virtualized by the outer CollectionView (RecyclerView).
        // Focus chrome: at 10 feet the 3px ring read as invisible even when
        // frames flowed — TV escalates to a 5px ring plus a subtle white lift
        // of the focused card itself (FocusedCardLift is a veil opacity;
        // render-level only, no layout or shadow work on the focus path).
        HomeLayoutClass.Tv => new(
            CardWidth: 300, CardHeight: 160, CardCornerRadius: 16, BadgeSize: 50,
            TeamFontSize: 18, ScoreFontSize: 30, StatusFontSize: 15, AggregateFontSize: 13,
            LeagueTitleFontSize: 24, LeagueIconSize: 40, PageTitleFontSize: 32, PageSubtitleFontSize: 17,
            PagePadding: 20, RowSpacing: 32, CardSpacing: 16, BrandLogoSize: 68,
            FlatCardChrome: true, StagedStripCards: 8,
            FocusRingThickness: 5, FocusedCardLift: 0.10),

        HomeLayoutClass.Desktop => new(
            CardWidth: 350, CardHeight: 192, CardCornerRadius: 14, BadgeSize: 54,
            TeamFontSize: 17, ScoreFontSize: 32, StatusFontSize: 13, AggregateFontSize: 12,
            LeagueTitleFontSize: 20, LeagueIconSize: 34, PageTitleFontSize: 28, PageSubtitleFontSize: 14,
            PagePadding: 32, RowSpacing: 40, CardSpacing: 16, BrandLogoSize: 58),

        // Phone size notch (field-tested down from 300x168 / 268x160 like the
        // TV notches): the cards dominated a phone screen — landscape showed
        // barely 2.5 cards, portrait one card plus a sliver. 272x150 / 244x140
        // keeps a fuller strip in view; badge/score/team scale proportionally
        // and hold arm's-length floors (badge >= 38, score >= 22, team >= 12
        // at typical phone density). Chrome (status chip, league header,
        // page furniture, spacing) is unchanged — it read fine in the field.
        // FlatCardChrome on phones too: shadows x ~37 cards (card drop shadow
        // + four badge blurs each) are the biggest raster line-item on ANY
        // renderer — phones are stronger than the TV box but spend that
        // strength on 60fps touch scrolling, and the TV evidence (the blurs
        // were a large slice of the full-tree pass) transfers directly. The
        // flat treatment also keeps phones visually consistent with TV.
        // Desktop keeps the full chrome: a handful of visible cards, real GPU
        // headroom, and a 2-foot viewing distance where the depth cues earn
        // their cost.
        HomeLayoutClass.PhoneLandscape => new(
            CardWidth: 272, CardHeight: 150, CardCornerRadius: 12, BadgeSize: 42,
            TeamFontSize: 13, ScoreFontSize: 24, StatusFontSize: 12, AggregateFontSize: 11,
            LeagueTitleFontSize: 17, LeagueIconSize: 28, PageTitleFontSize: 22, PageSubtitleFontSize: 13,
            PagePadding: 20, RowSpacing: 20, CardSpacing: 12, BrandLogoSize: 46,
            FlatCardChrome: true),

        HomeLayoutClass.PhonePortrait => new(
            CardWidth: 244, CardHeight: 140, CardCornerRadius: 12, BadgeSize: 38,
            TeamFontSize: 12, ScoreFontSize: 22, StatusFontSize: 12, AggregateFontSize: 11,
            LeagueTitleFontSize: 16, LeagueIconSize: 26, PageTitleFontSize: 20, PageSubtitleFontSize: 12,
            PagePadding: 14, RowSpacing: 18, CardSpacing: 10, BrandLogoSize: 40,
            FlatCardChrome: true),

        _ => For(HomeLayoutClass.Desktop),
    };
}
