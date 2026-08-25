namespace VardyParty.Catalog;

public interface ILeagueFilterPreferencesStore
{
    bool HasSavedPreferences { get; }

    IReadOnlySet<string> LoadHiddenLeagues();

    void SaveHiddenLeagues(IReadOnlySet<string> hiddenLeagues);

    void ClearSavedPreferences();
}
