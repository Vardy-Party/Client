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

    /// <summary>Row height for the horizontal card strip: card + focus-scale headroom.</summary>
    public double RowHeight => _metrics.CardHeight + 24;

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
        nameof(RowHeight), nameof(PagePaddingThickness), nameof(RowMarginThickness), nameof(CardMarginThickness),
        nameof(CardSpacing), nameof(BrandLogoSize),
    ];
}
