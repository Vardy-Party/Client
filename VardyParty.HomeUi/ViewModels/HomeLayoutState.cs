using System.ComponentModel;
using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// Bindable view of the current <see cref="HomeLayoutMetrics"/>. One shared
/// instance is handed to every row/card VM; when the window is resized into a
/// different <see cref="HomeLayoutClass"/> all bindings refresh at once.
/// </summary>
public sealed class HomeLayoutState : INotifyPropertyChanged
{
    private HomeLayoutMetrics _metrics = HomeLayoutMetrics.For(HomeLayoutClass.Desktop);

    public event PropertyChangedEventHandler? PropertyChanged;

    public HomeLayoutClass Class { get; private set; } = HomeLayoutClass.Desktop;

    public bool IsTv => Class == HomeLayoutClass.Tv;

    public double CardWidth => _metrics.CardWidth;
    public double CardHeight => _metrics.CardHeight;
    public double CardCornerRadius => _metrics.CardCornerRadius;
    public double BadgeSize => _metrics.BadgeSize;
    public double TeamFontSize => _metrics.TeamFontSize;
    public double ScoreFontSize => _metrics.ScoreFontSize;
    public double StatusFontSize => _metrics.StatusFontSize;
    public double AggregateFontSize => _metrics.AggregateFontSize;
    public double LeagueTitleFontSize => _metrics.LeagueTitleFontSize;
    public double LeagueIconSize => _metrics.LeagueIconSize;
    public double PageTitleFontSize => _metrics.PageTitleFontSize;
    public double PageSubtitleFontSize => _metrics.PageSubtitleFontSize;
    public double BrandLogoSize => _metrics.BrandLogoSize;

    /// <summary>TV raster budget: no per-card shadows, cheap team wash.</summary>
    public bool FlatCardChrome => _metrics.FlatCardChrome;

    /// <summary>
    /// Strip staging: a NEW row materializes at most this many cards up
    /// front; the rest are appended in dispatcher-idle chunks. 0 = full
    /// materialization (non-TV classes).
    /// </summary>
    public int StagedStripCards => _metrics.StagedStripCards;

    /// <summary>Focus ring stroke: 5px on TV (10-foot visibility), 3px elsewhere.</summary>
    public double FocusRingThickness => _metrics.FocusRingThickness;

    /// <summary>Opacity of the white focus veil lifting the focused card (TV only; 0 = off).</summary>
    public double FocusedCardLift => _metrics.FocusedCardLift;

    /// <summary>
    /// Row height for the horizontal card strip: card + room for the
    /// edge-aligned focus ring (half-stroke outside) and comfort pad —
    /// derived from <see cref="Views.TvFocusScrollMath"/>.
    /// </summary>
    public double RowHeight => _metrics.CardHeight + (2 * StripChromePadVertical);

    /// <summary>
    /// Explicit height of one league-row item (header + strip), NOT including
    /// <see cref="RowMarginThickness"/>. CollectionView on every MAUI backend
    /// otherwise stretches items to the leftover viewport — header and cards
    /// at the top of a tall cell, empty black below (the field slab). Pinning
    /// HeightRequest to this value makes that empty region zero height.
    /// Header line is the taller of the league icon and the title's line box;
    /// 10 is the Spacing between header and strip in LeagueRowTemplate.
    /// </summary>
    public double LeagueRowHeight =>
        Math.Max(LeagueIconSize, LeagueTitleFontSize * 1.4) + 10 + RowHeight;

    /// <summary>
    /// Padding of the strip's inner card layout: half the edge-aligned focus
    /// ring plus comfort pad so the ring is not sheared at the strip edge.
    /// Uniform across layout classes.
    /// </summary>
    public Thickness StripPaddingThickness => new(
        Views.TvFocusScrollMath.FocusChromePadding(_metrics.CardWidth, _metrics.FocusRingThickness),
        StripChromePadVertical);

    private double StripChromePadVertical =>
        Views.TvFocusScrollMath.FocusChromePadding(_metrics.CardHeight, _metrics.FocusRingThickness);

    public Thickness PagePaddingThickness => new(_metrics.PagePadding);

    /// <summary>
    /// Inter-league gap ABOVE each row (not below): the space belongs between
    /// sections, so a league header binds visually to its own card strip
    /// instead of floating under the previous league's cards.
    /// </summary>
    public Thickness RowMarginThickness => new(0, _metrics.RowSpacing, 0, 0);
    public Thickness CardMarginThickness => new(0, 0, _metrics.CardSpacing, 0);
    public double CardSpacing => _metrics.CardSpacing;

    public void Apply(HomeLayoutClass layoutClass)
    {
        if (layoutClass == Class) return;

        Class = layoutClass;
        _metrics = HomeLayoutMetrics.For(layoutClass);

        foreach (var name in ChangedNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static readonly string[] ChangedNames =
    [
        nameof(Class), nameof(IsTv),
        nameof(CardWidth), nameof(CardHeight), nameof(CardCornerRadius), nameof(BadgeSize),
        nameof(TeamFontSize), nameof(ScoreFontSize), nameof(StatusFontSize), nameof(AggregateFontSize),
        nameof(LeagueTitleFontSize), nameof(LeagueIconSize), nameof(PageTitleFontSize), nameof(PageSubtitleFontSize),
        nameof(RowHeight), nameof(LeagueRowHeight), nameof(StripPaddingThickness), nameof(PagePaddingThickness), nameof(RowMarginThickness), nameof(CardMarginThickness),
        nameof(CardSpacing), nameof(BrandLogoSize), nameof(FlatCardChrome), nameof(StagedStripCards),
        nameof(FocusRingThickness), nameof(FocusedCardLift),
    ];
}
