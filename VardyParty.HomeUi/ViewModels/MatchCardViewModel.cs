using System.ComponentModel;
using VardyParty.Kernel;
using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// Everything one match card needs: teams, badges, in-game/aggregate scores,
/// status (minutes, injury time, HT/FT, extra time, penalties), kick-off time,
/// and the team-colour wash brushes for the "ephemeral graphics" background.
/// Mutable in place: catalog refreshes update the SAME instance via
/// <see cref="UpdateFrom"/> (INPC per property) instead of re-materializing
/// the card view — the Clear+rebuild-per-poll path saturated the TV box.
/// </summary>
public sealed class MatchCardViewModel : INotifyPropertyChanged
{
    private readonly Action<MatchCardViewModel> _onPicked;
    private readonly Action<MatchCardViewModel>? _onFocused;
    private ImageSource? _homeBadge;
    private ImageSource? _awayBadge;
    private string _homeTeam = string.Empty;
    private string _awayTeam = string.Empty;
    private string _homeInitial = "?";
    private string _awayInitial = "?";
    private MatchPhase _phase;
    private bool _isLive;
    private string _statusText = string.Empty;
    private bool _hasScore;
    private string _scoreText = string.Empty;
    private string _aggregateText = string.Empty;
    private Brush _homeAccent = new SolidColorBrush(Colors.Transparent);
    private Brush _awayAccent = new SolidColorBrush(Colors.Transparent);
    private Brush _cardBackground = new SolidColorBrush(Colors.Transparent);
    private string? _homeBadgeUrl;
    private string? _awayBadgeUrl;

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
        ApplyGame(game);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Game Game { get; private set; }
    public HomeLayoutState Layout { get; }

    public string HomeTeam
    {
        get => _homeTeam;
        private set => SetString(ref _homeTeam, value, nameof(HomeTeam));
    }

    public string AwayTeam
    {
        get => _awayTeam;
        private set => SetString(ref _awayTeam, value, nameof(AwayTeam));
    }

    public string HomeInitial
    {
        get => _homeInitial;
        private set => SetString(ref _homeInitial, value, nameof(HomeInitial));
    }

    public string AwayInitial
    {
        get => _awayInitial;
        private set => SetString(ref _awayInitial, value, nameof(AwayInitial));
    }

    public MatchPhase Phase
    {
        get => _phase;
        private set
        {
            if (_phase == value) return;
            _phase = value;
            Raise(nameof(Phase));
        }
    }

    public bool IsLive
    {
        get => _isLive;
        private set
        {
            if (_isLive == value) return;
            _isLive = value;
            Raise(nameof(IsLive));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetString(ref _statusText, value, nameof(StatusText));
    }

    public bool HasScore
    {
        get => _hasScore;
        private set
        {
            if (_hasScore == value) return;
            _hasScore = value;
            Raise(nameof(HasScore));
            Raise(nameof(ScoreIsDim));
        }
    }

    public string ScoreText
    {
        get => _scoreText;
        private set => SetString(ref _scoreText, value, nameof(ScoreText));
    }

    public string AggregateText
    {
        get => _aggregateText;
        private set
        {
            if (_aggregateText == value) return;
            var hadAggregate = HasAggregate;
            _aggregateText = value;
            Raise(nameof(AggregateText));
            if (hadAggregate != HasAggregate)
            {
                Raise(nameof(HasAggregate));
            }
        }
    }

    public bool HasAggregate => _aggregateText.Length > 0;
    public bool ScoreIsDim => !HasScore;

    public Brush HomeAccent
    {
        get => _homeAccent;
        private set => SetBrush(ref _homeAccent, value, nameof(HomeAccent));
    }

    public Brush AwayAccent
    {
        get => _awayAccent;
        private set => SetBrush(ref _awayAccent, value, nameof(AwayAccent));
    }

    public Brush CardBackground
    {
        get => _cardBackground;
        private set => SetBrush(ref _cardBackground, value, nameof(CardBackground));
    }

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

    private bool _isResolving;

    /// <summary>
    /// The user picked this card and stream resolution is in flight: the card
    /// keeps a distinct "selected" treatment so a TV click visibly took.
    /// Set by <see cref="HomeViewModel"/>, cleared via
    /// <see cref="HomeViewModel.OnStreamResolutionEnded"/>.
    /// </summary>
    public bool IsResolving
    {
        get => _isResolving;
        set
        {
            if (_isResolving == value) return;
            _isResolving = value;
            Raise(nameof(IsResolving));
        }
    }

    /// <summary>
    /// TV D-pad: armed on the first card of the first row when the grid first
    /// appears; the view consumes it once (never re-fires on later refreshes).
    /// </summary>
    public bool RequestsInitialFocus { get; set; }

    /// <summary>One-shot latch for the armed initial focus.</summary>
    public bool TryConsumeInitialFocus()
    {
        if (!RequestsInitialFocus) return false;
        RequestsInitialFocus = false;
        return true;
    }

    public void Pick() => _onPicked(this);

    /// <summary>Keyboard/D-pad focus or pointer highlight landed on this card.</summary>
    public void FocusMoved() => _onFocused?.Invoke(this);

    /// <summary>
    /// Refresh this card from the same fixture on a newer poll (identity per
    /// <see cref="HomeBoardDiffer.GameKey"/>). Only changed properties raise
    /// INPC, so an unchanged card causes zero binding churn. A changed badge
    /// URL (BBC enrichment landing) clears the stale badge so the image pass
    /// reloads it.
    /// </summary>
    public void UpdateFrom(Game game)
    {
        Game = game;
        ApplyGame(game);
    }

    private void ApplyGame(Game game)
    {
        var homeTeam = game.DisplayHome;
        var awayTeam = game.DisplayAway;
        var teamsChanged = homeTeam != _homeTeam || awayTeam != _awayTeam;
        HomeTeam = homeTeam;
        AwayTeam = awayTeam;
        HomeInitial = FirstLetter(homeTeam);
        AwayInitial = FirstLetter(awayTeam);

        Phase = MatchStatusPresenter.GetPhase(game);
        IsLive = MatchStatusPresenter.IsLivePhase(Phase);
        StatusText = MatchStatusPresenter.GetStatusText(game);
        HasScore = MatchStatusPresenter.HasScore(game);
        ScoreText = MatchStatusPresenter.GetScoreText(game);
        AggregateText = MatchStatusPresenter.GetAggregateText(game) ?? string.Empty;

        if (teamsChanged)
        {
            var homeColors = TeamPalette.GetColors(homeTeam);
            var awayColors = TeamPalette.GetColors(awayTeam);
            HomeAccent = new SolidColorBrush(Color.FromArgb(homeColors.Primary));
            AwayAccent = new SolidColorBrush(Color.FromArgb(awayColors.Primary));
            CardBackground = BuildWash(homeColors.Primary, awayColors.Primary);
        }

        if (!string.Equals(_homeBadgeUrl, game.HomeBadgeUrl, StringComparison.OrdinalIgnoreCase))
        {
            _homeBadgeUrl = game.HomeBadgeUrl;
            HomeBadge = null;
        }

        if (!string.Equals(_awayBadgeUrl, game.AwayBadgeUrl, StringComparison.OrdinalIgnoreCase))
        {
            _awayBadgeUrl = game.AwayBadgeUrl;
            AwayBadge = null;
        }
    }

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

    private void SetString(ref string field, string value, string name)
    {
        if (field == value) return;
        field = value;
        Raise(name);
    }

    private void SetBrush(ref Brush field, Brush value, string name)
    {
        if (ReferenceEquals(field, value)) return;
        field = value;
        Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
