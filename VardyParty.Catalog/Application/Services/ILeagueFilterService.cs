using VardyParty.Models;

namespace VardyParty.Catalog;

public interface ILeagueFilterService
{
    IReadOnlySet<string> DefaultHiddenLeagues { get; }

    IReadOnlySet<string> HiddenLeagues { get; }

    event Action? Changed;

    bool IsLeagueVisible(string? league);

    List<Game> FilterGames(IEnumerable<Game>? games);

    IReadOnlyList<string> GetKnownLeagues(IDictionary<string, List<Game>>? gamesByLeague);

    void SetLeagueVisible(string league, bool visible);

    void SetLeaguesVisible(IEnumerable<string> leagues, bool visible);

    void ResetToDefaults();
}
