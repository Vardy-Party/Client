using VardyParty.Models;

namespace VardyParty.Catalog;

public class LeagueFilterService : ILeagueFilterService
{
    private readonly ILeagueFilterPreferencesStore _store;
    private readonly HashSet<string> _hiddenLeagues;

    public LeagueFilterService(ILeagueFilterPreferencesStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hiddenLeagues = _store.HasSavedPreferences
            ? new HashSet<string>(_store.LoadHiddenLeagues(), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(LeagueFilterDefaults.HiddenLeagues, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> DefaultHiddenLeagues => LeagueFilterDefaults.HiddenLeagues;

    public IReadOnlySet<string> HiddenLeagues => _hiddenLeagues;

    public event Action? Changed;

    public bool IsLeagueVisible(string? league)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return true;
        }

        return !_hiddenLeagues.Contains(league);
    }

    public List<Game> FilterGames(IEnumerable<Game>? games)
    {
        if (games == null)
        {
            return new List<Game>();
        }

        return games.Where(g => IsLeagueVisible(g.League)).ToList();
    }

    public IReadOnlyList<string> GetKnownLeagues(IDictionary<string, List<Game>>? gamesByLeague)
    {
        if (gamesByLeague == null || gamesByLeague.Count == 0)
        {
            return Array.Empty<string>();
        }

        return gamesByLeague.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetLeagueVisible(string league, bool visible)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return;
        }

        var changed = visible
            ? _hiddenLeagues.Remove(league)
            : _hiddenLeagues.Add(league);

        if (!changed)
        {
            return;
        }

        PersistAndNotify();
    }

    public void SetLeaguesVisible(IEnumerable<string> leagues, bool visible)
    {
        if (leagues == null)
        {
            return;
        }

        var changed = false;
        foreach (var league in leagues)
        {
            if (string.IsNullOrWhiteSpace(league))
            {
                continue;
            }

            changed = visible
                ? _hiddenLeagues.Remove(league) || changed
                : _hiddenLeagues.Add(league) || changed;
        }

        if (!changed)
        {
            return;
        }

        PersistAndNotify();
    }

    public void ResetToDefaults()
    {
        _hiddenLeagues.Clear();
        foreach (var league in LeagueFilterDefaults.HiddenLeagues)
        {
            _hiddenLeagues.Add(league);
        }

        _store.ClearSavedPreferences();
        Changed?.Invoke();
    }

    private void PersistAndNotify()
    {
        Persist();
        Changed?.Invoke();
    }

    private void Persist()
    {
        _store.SaveHiddenLeagues(_hiddenLeagues);
    }
}
