namespace VardyParty.Catalog;

public class InMemoryLeagueFilterPreferencesStore : ILeagueFilterPreferencesStore
{
    private HashSet<string>? _hiddenLeagues;

    public bool HasSavedPreferences => _hiddenLeagues != null;

    public IReadOnlySet<string> LoadHiddenLeagues()
    {
        return _hiddenLeagues ?? new HashSet<string>(LeagueFilterDefaults.HiddenLeagues, StringComparer.OrdinalIgnoreCase);
    }

    public void SaveHiddenLeagues(IReadOnlySet<string> hiddenLeagues)
    {
        _hiddenLeagues = new HashSet<string>(hiddenLeagues, StringComparer.OrdinalIgnoreCase);
    }

    public void ClearSavedPreferences()
    {
        _hiddenLeagues = null;
    }
}
