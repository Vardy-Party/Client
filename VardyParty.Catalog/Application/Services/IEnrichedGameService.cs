using VardyParty.Kernel;

namespace VardyParty.Catalog;

public interface IEnrichedGameService
{
    // Live stream of updates
    IObservable<Dictionary<string, List<Game>>?> GamesStream { get; }

    Dictionary<string, List<Game>>? GetLatestGames();

    // Stream of error messages (null when no error)
    IObservable<string?> ErrorStream { get; }
}
