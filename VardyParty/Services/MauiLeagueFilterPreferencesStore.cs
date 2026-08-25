using System.Text.Json;
using Microsoft.Maui.Storage;

namespace VardyParty.MauiServices;

public class MauiLeagueFilterPreferencesStore : ILeagueFilterPreferencesStore
{
    private const string PreferencesKey = "league_filter_hidden";

    public bool HasSavedPreferences => Preferences.ContainsKey(PreferencesKey);

    public IReadOnlySet<string> LoadHiddenLeagues()
    {
        if (!HasSavedPreferences)
        {
            return new HashSet<string>(LeagueFilterDefaults.HiddenLeagues, StringComparer.OrdinalIgnoreCase);
        }

        var json = Preferences.Get(PreferencesKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var leagues = JsonSerializer.Deserialize<List<string>>(json);
            return new HashSet<string>(leagues ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveHiddenLeagues(IReadOnlySet<string> hiddenLeagues)
    {
        var json = JsonSerializer.Serialize(hiddenLeagues.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList());
        Preferences.Set(PreferencesKey, json);
    }

    public void ClearSavedPreferences()
    {
        Preferences.Remove(PreferencesKey);
    }
}
