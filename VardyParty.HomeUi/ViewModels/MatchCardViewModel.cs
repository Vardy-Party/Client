using System.ComponentModel;
using VardyParty.Kernel;
using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// Everything one match card needs: teams, badges, in-game/aggregate scores,
/// status (minutes, injury time, HT/FT, extra time, penalties), kick-off time,
/// and the team-colour wash brushes for the "ephemeral graphics" background.
/// </summary>
public sealed class MatchCardViewModel : INotifyPropertyChanged
{
    private readonly Action<MatchCardViewModel> _onPicked;
    private readonly Action<MatchCardViewModel>? _onFocused;
    private ImageSource? _homeBadge;
    private ImageSource? _awayBadge;

    public MatchCardViewModel(
        Game game,
        HomeLayoutState layout,
        Action<MatchCardViewModel> onPicked,
        Action<MatchCardViewModel>? onFocused = null)
    {
        Game = game;
        Layout = layout;
        _onPicked = onPicked;
        _onFocused = onFocused;

        HomeTeam = game.DisplayHome;
        AwayTeam = game.DisplayAway;
        HomeInitial = FirstLetter(HomeTeam);
        AwayInitial = FirstLetter(AwayTeam);

        Phase = MatchStatusPresenter.GetPhase(game);
        IsLive = MatchStatusPresenter.IsLivePhase(Phase);
        StatusText = MatchStatusPresenter.GetStatusText(game);
        HasScore = MatchStatusPresenter.HasScore(game);
        ScoreText = MatchStatusPresenter.GetScoreText(game);
        AggregateText = MatchStatusPresenter.GetAggregateText(game) ?? string.Empty;
        HasAggregate = AggregateText.Length > 0;

        var homeColors = TeamPalette.GetColors(HomeTeam);
        var awayColors = TeamPalette.GetColors(AwayTeam);
        HomeAccent = new SolidColorBrush(Color.FromArgb(homeColors.Primary));
        AwayAccent = new SolidColorBrush(Color.FromArgb(awayColors.Primary));
        CardBackground = BuildWash(homeColors.Primary, awayColors.Primary);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Game Game { get; }
    public HomeLayoutState Layout { get; }

    public string HomeTeam { get; }
    public string AwayTeam { get; }
    public string HomeInitial { get; }
    public string AwayInitial { get; }

    public MatchPhase Phase { get; }
    public bool IsLive { get; }
    public string StatusText { get; }
    public bool HasScore { get; }
    public string ScoreText { get; }
    public string AggregateText { get; }
    public bool HasAggregate { get; }
    public bool ScoreIsDim => !HasScore;

    public Brush HomeAccent { get; }
    public Brush AwayAccent { get; }
    public Brush CardBackground { get; }

    public ImageSource? HomeBadge
    {
        get => _homeBadge;
        set
        {
            if (ReferenceEquals(_homeBadge, value)) return;
            _homeBadge = value;
            Raise(nameof(HomeBadge));
            Raise(nameof(HasHomeBadge));
            Raise(nameof(NoHomeBadge));
        }
    }

    public ImageSource? AwayBadge
    {
        get => _awayBadge;
        set
        {
            if (ReferenceEquals(_awayBadge, value)) return;
            _awayBadge = value;
            Raise(nameof(AwayBadge));
            Raise(nameof(HasAwayBadge));
            Raise(nameof(NoAwayBadge));
        }
    }

    public bool HasHomeBadge => _homeBadge != null;
    public bool HasAwayBadge => _awayBadge != null;
    public bool NoHomeBadge => _homeBadge == null;
    public bool NoAwayBadge => _awayBadge == null;

    public void Pick() => _onPicked(this);

    /// <summary>Keyboard/D-pad focus or pointer highlight landed on this card.</summary>
    public void FocusMoved() => _onFocused?.Invoke(this);

    /// <summary>
    /// Diagonal wash: home colour bleeds in from the top-left, away colour
    /// from the bottom-right, with the dark card base showing through the middle.
    /// </summary>
    private static Brush BuildWash(string homeHex, string awayHex)
    {
        var home = WithAlpha(Color.FromArgb(homeHex), 0x6E);
        var away = WithAlpha(Color.FromArgb(awayHex), 0x6E);

        return new LinearGradientBrush(
            [
                new GradientStop(home, 0.0f),
                new GradientStop(WithAlpha(home, 0x00), 0.46f),
                new GradientStop(WithAlpha(away, 0x00), 0.54f),
                new GradientStop(away, 1.0f),
            ],
            new Point(0, 0),
            new Point(1, 1));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromRgba(color.Red, color.Green, color.Blue, alpha / 255f);

    private static string FirstLetter(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.TrimStart()[..1].ToUpperInvariant();

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
