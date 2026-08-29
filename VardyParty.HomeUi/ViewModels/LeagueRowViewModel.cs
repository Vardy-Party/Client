using System.Collections.ObjectModel;
using System.ComponentModel;

namespace VardyParty.HomeUi;

/// <summary>
/// One horizontally-scrolling league row on the homepage. Long-lived: catalog
/// refreshes mutate <see cref="Cards"/> in place and call <see cref="Refresh"/>
/// instead of replacing the row, so the materialized strip (and its league
/// icon) survives every poll.
/// </summary>
public sealed class LeagueRowViewModel : INotifyPropertyChanged
{
    private ImageSource? _leagueIcon;
    private bool _hasLiveGames;
    private string _matchCountText = string.Empty;

    public LeagueRowViewModel(string league, bool hasLiveGames, IReadOnlyList<MatchCardViewModel> cards, HomeLayoutState layout)
    {
        League = league;
        Layout = layout;
        Cards = new ObservableCollection<MatchCardViewModel>(cards);
        _hasLiveGames = hasLiveGames;
        _matchCountText = FormatMatchCount(cards.Count);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string League { get; }
    public HomeLayoutState Layout { get; }
    public ObservableCollection<MatchCardViewModel> Cards { get; }

    public bool HasLiveGames
    {
        get => _hasLiveGames;
        private set
        {
            if (_hasLiveGames == value) return;
            _hasLiveGames = value;
            Raise(nameof(HasLiveGames));
        }
    }

    public string MatchCountText
    {
        get => _matchCountText;
        private set
        {
            if (_matchCountText == value) return;
            _matchCountText = value;
            Raise(nameof(MatchCountText));
        }
    }

    public ImageSource? LeagueIcon
    {
        get => _leagueIcon;
        set
        {
            if (ReferenceEquals(_leagueIcon, value)) return;
            _leagueIcon = value;
            Raise(nameof(LeagueIcon));
            Raise(nameof(HasLeagueIcon));
        }
    }

    public bool HasLeagueIcon => _leagueIcon != null;

    /// <summary>Called after an in-place card diff so the header chips follow.</summary>
    public void Refresh(bool hasLiveGames)
    {
        HasLiveGames = hasLiveGames;
        MatchCountText = FormatMatchCount(Cards.Count);
    }

    private static string FormatMatchCount(int count) =>
        count == 1 ? "1 match" : $"{count} matches";

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
