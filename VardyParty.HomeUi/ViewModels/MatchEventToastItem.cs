using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// One toast's display payload, built at event time from whatever the board
/// already has materialized: league icon and team badges are borrowed from
/// the live row/card VMs when present (a staged/hidden card simply has none —
/// the monogram initials cover that), and the tint is the two teams'
/// <see cref="TeamPalette"/> colours as a subdued wash.
/// </summary>
public sealed class MatchEventToastItem
{
    public MatchEventToastItem(
        MatchEvent matchEvent,
        ImageSource? leagueIcon,
        ImageSource? homeBadge,
        ImageSource? awayBadge)
    {
        Event = matchEvent;
        Headline = MatchEventPresenter.Headline(matchEvent);
        LeagueName = MatchEventPresenter.LeagueName(matchEvent);
        GameKey = HomeBoardDiffer.GameKey(matchEvent.Game);
        LeagueIcon = leagueIcon;
        HomeBadge = homeBadge;
        AwayBadge = awayBadge;
        HomeInitial = FirstLetter(matchEvent.Game.DisplayHome);
        AwayInitial = FirstLetter(matchEvent.Game.DisplayAway);

        var homeColors = TeamPalette.GetColors(matchEvent.Game.DisplayHome);
        var awayColors = TeamPalette.GetColors(matchEvent.Game.DisplayAway);
        HomeAccent = new SolidColorBrush(Color.FromArgb(homeColors.Primary));
        AwayAccent = new SolidColorBrush(Color.FromArgb(awayColors.Primary));

        // Same cheap 2-stop horizontal wash the flat card chrome uses: the
        // toast is transient, so it must never cost shadow/gradient raster
        // headroom on the TV box.
        TintBrush = new LinearGradientBrush(
            [
                new GradientStop(WithAlpha(Color.FromArgb(homeColors.Primary), 0x42), 0.0f),
                new GradientStop(WithAlpha(Color.FromArgb(awayColors.Primary), 0x42), 1.0f),
            ],
            new Point(0, 0),
            new Point(1, 0));
    }

    public MatchEvent Event { get; }
    public string Headline { get; }
    public string LeagueName { get; }

    /// <summary>Card identity (<see cref="HomeBoardDiffer.GameKey"/>) for the synchronized flash.</summary>
    public string GameKey { get; }

    public ImageSource? LeagueIcon { get; }
    public ImageSource? HomeBadge { get; }
    public ImageSource? AwayBadge { get; }
    public bool HasLeagueIcon => LeagueIcon != null;
    public bool HasHomeBadge => HomeBadge != null;
    public bool NoHomeBadge => HomeBadge == null;
    public bool HasAwayBadge => AwayBadge != null;
    public bool NoAwayBadge => AwayBadge == null;
    public string HomeInitial { get; }
    public string AwayInitial { get; }
    public Brush HomeAccent { get; }
    public Brush AwayAccent { get; }
    public Brush TintBrush { get; }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromRgba(color.Red, color.Green, color.Blue, alpha / 255f);

    private static string FirstLetter(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.TrimStart()[..1].ToUpperInvariant();
}
