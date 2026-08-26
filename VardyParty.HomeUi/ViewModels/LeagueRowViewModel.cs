using System.Collections.ObjectModel;
using System.ComponentModel;

namespace VardyParty.HomeUi;

/// <summary>One horizontally-scrolling league row on the homepage.</summary>
public sealed class LeagueRowViewModel : INotifyPropertyChanged
{
    private ImageSource? _leagueIcon;

    public LeagueRowViewModel(string league, bool hasLiveGames, IReadOnlyList<MatchCardViewModel> cards, HomeLayoutState layout)
    {
        League = league;
        HasLiveGames = hasLiveGames;
        Layout = layout;
        Cards = new ObservableCollection<MatchCardViewModel>(cards);
        MatchCountText = cards.Count == 1 ? "1 match" : $"{cards.Count} matches";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string League { get; }
    public bool HasLiveGames { get; }
    public string MatchCountText { get; }
    public HomeLayoutState Layout { get; }
    public ObservableCollection<MatchCardViewModel> Cards { get; }

    public ImageSource? LeagueIcon
    {
        get => _leagueIcon;
        set
        {
            if (ReferenceEquals(_leagueIcon, value)) return;
            _leagueIcon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LeagueIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLeagueIcon)));
        }
    }

    public bool HasLeagueIcon => _leagueIcon != null;
}
