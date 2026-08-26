using VardyParty.Catalog;
using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Shared flyout/menu presentation. Blazor AppMenu binds to this; Linux can later.
/// </summary>
public sealed class MenuViewModel
{
    private readonly ILeagueFilterService _leagueFilter;
    private readonly UiSoundService _uiSounds;
    private List<string> _knownLeagues = new();

    public MenuViewModel(ILeagueFilterService leagueFilter, UiSoundService uiSounds)
    {
        _leagueFilter = leagueFilter ?? throw new ArgumentNullException(nameof(leagueFilter));
        _uiSounds = uiSounds ?? throw new ArgumentNullException(nameof(uiSounds));
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

    /// <summary>Settings: the persisted "UI sounds" switch (default ON).</summary>
    public bool UiSoundsEnabled => _uiSounds.Enabled;

    /// <summary>Flips the switch; turning ON plays the Select sound as confirmation.</summary>
    public void ToggleUiSounds() => _uiSounds.SetEnabled(!_uiSounds.Enabled);
}
