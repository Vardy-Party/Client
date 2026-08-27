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
    bool FlatCardChrome = false)
{
    public static HomeLayoutMetrics For(HomeLayoutClass layoutClass) => layoutClass switch
    {
        // 10-foot UI: readable from the sofa without dominating the panel.
        // Field-tested down twice from 440x232 (barely 2.5 rows on 1080p,
        // reported oversized). 340x180 keeps 5 cards per row and ~3.6 league
        // rows visible; type/badge sizes stay at the 10-foot floors (status
        // chip >= 15, score >= 34, badge >= 56) and still fit the card box
        // (inner 312x158: status row + badge + two team-name lines).
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
        HomeLayoutClass.Tv => new(
            CardWidth: 340, CardHeight: 180, CardCornerRadius: 16, BadgeSize: 56,
            TeamFontSize: 19, ScoreFontSize: 34, StatusFontSize: 15, AggregateFontSize: 13,
            LeagueTitleFontSize: 24, LeagueIconSize: 40, PageTitleFontSize: 32, PageSubtitleFontSize: 17,
            PagePadding: 44, RowSpacing: 32, CardSpacing: 16, BrandLogoSize: 68,
            FlatCardChrome: true),

        HomeLayoutClass.Desktop => new(
            CardWidth: 350, CardHeight: 192, CardCornerRadius: 14, BadgeSize: 54,
            TeamFontSize: 17, ScoreFontSize: 32, StatusFontSize: 13, AggregateFontSize: 12,
            LeagueTitleFontSize: 20, LeagueIconSize: 34, PageTitleFontSize: 28, PageSubtitleFontSize: 14,
            PagePadding: 32, RowSpacing: 40, CardSpacing: 16, BrandLogoSize: 58),

        HomeLayoutClass.PhoneLandscape => new(
            CardWidth: 300, CardHeight: 168, CardCornerRadius: 12, BadgeSize: 46,
            TeamFontSize: 14, ScoreFontSize: 26, StatusFontSize: 12, AggregateFontSize: 11,
            LeagueTitleFontSize: 17, LeagueIconSize: 28, PageTitleFontSize: 22, PageSubtitleFontSize: 13,
            PagePadding: 20, RowSpacing: 20, CardSpacing: 12, BrandLogoSize: 46),

        HomeLayoutClass.PhonePortrait => new(
            CardWidth: 268, CardHeight: 160, CardCornerRadius: 12, BadgeSize: 42,
            TeamFontSize: 13, ScoreFontSize: 24, StatusFontSize: 12, AggregateFontSize: 11,
            LeagueTitleFontSize: 16, LeagueIconSize: 26, PageTitleFontSize: 20, PageSubtitleFontSize: 12,
            PagePadding: 14, RowSpacing: 18, CardSpacing: 10, BrandLogoSize: 40),

        _ => For(HomeLayoutClass.Desktop),
    };
}
