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
    double CardSpacing)
{
    public static HomeLayoutMetrics For(HomeLayoutClass layoutClass) => layoutClass switch
    {
        // 10-foot UI: big targets, generous spacing, readable from the sofa.
        HomeLayoutClass.Tv => new(
            CardWidth: 440, CardHeight: 232, CardCornerRadius: 18, BadgeSize: 68,
            TeamFontSize: 22, ScoreFontSize: 40, StatusFontSize: 17, AggregateFontSize: 15,
            LeagueTitleFontSize: 26, LeagueIconSize: 34, PageTitleFontSize: 34, PageSubtitleFontSize: 18,
            PagePadding: 48, RowSpacing: 36, CardSpacing: 20),

        HomeLayoutClass.Desktop => new(
            CardWidth: 350, CardHeight: 192, CardCornerRadius: 14, BadgeSize: 54,
            TeamFontSize: 17, ScoreFontSize: 32, StatusFontSize: 13, AggregateFontSize: 12,
            LeagueTitleFontSize: 20, LeagueIconSize: 26, PageTitleFontSize: 28, PageSubtitleFontSize: 14,
            PagePadding: 32, RowSpacing: 28, CardSpacing: 16),

        HomeLayoutClass.PhoneLandscape => new(
            CardWidth: 300, CardHeight: 168, CardCornerRadius: 12, BadgeSize: 46,
            TeamFontSize: 14, ScoreFontSize: 26, StatusFontSize: 12, AggregateFontSize: 11,
            LeagueTitleFontSize: 17, LeagueIconSize: 22, PageTitleFontSize: 22, PageSubtitleFontSize: 13,
            PagePadding: 20, RowSpacing: 20, CardSpacing: 12),

        HomeLayoutClass.PhonePortrait => new(
            CardWidth: 268, CardHeight: 160, CardCornerRadius: 12, BadgeSize: 42,
            TeamFontSize: 13, ScoreFontSize: 24, StatusFontSize: 12, AggregateFontSize: 11,
            LeagueTitleFontSize: 16, LeagueIconSize: 20, PageTitleFontSize: 20, PageSubtitleFontSize: 12,
            PagePadding: 14, RowSpacing: 18, CardSpacing: 10),

        _ => For(HomeLayoutClass.Desktop),
    };
}
