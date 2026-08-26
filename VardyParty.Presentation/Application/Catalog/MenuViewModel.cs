using VardyParty.Catalog;
using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Shared flyout/menu presentation. Blazor AppMenu binds to this; Linux can later.
/// </summary>
public sealed class MenuViewModel
{
    private readonly ILeagueFilterService _leagueFilter;
    private List<string> _knownLeagues = new();

    public MenuViewModel(ILeagueFilterService leagueFilter)
    {
        _leagueFilter = leagueFilter ?? throw new ArgumentNullException(nameof(leagueFilter));
    }

    public IReadOnlyList<string> KnownLeagues => _knownLeagues;

    public void RefreshKnownLeagues(IDictionary<string, List<Game>>? gamesByLeague)
    {
        _knownLeagues = _leagueFilter.GetKnownLeagues(gamesByLeague).ToList();
    }

    public bool IsLeagueVisible(string league) => _leagueFilter.IsLeagueVisible(league);

    public void ToggleLeague(string league) =>
        _leagueFilter.SetLeagueVisible(league, !_leagueFilter.IsLeagueVisible(league));

    public void ShowAllLeagues() => _leagueFilter.SetLeaguesVisible(_knownLeagues, true);

    public void ResetToDefaults() => _leagueFilter.ResetToDefaults();
}
