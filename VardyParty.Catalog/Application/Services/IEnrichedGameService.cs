using System.Reactive;
using VardyParty.Kernel;

namespace VardyParty.Catalog;

public interface IEnrichedGameService
{
    // Live stream of updates
    IObservable<Dictionary<string, List<Game>>?> GamesStream { get; }

    Dictionary<string, List<Game>>? GetLatestGames();

    // Stream of error messages (null when no error)
    IObservable<string?> ErrorStream { get; }

    /// <summary>
    /// Fires when the games catalog returns 401. A genuine empty night is a
    /// normal board publish, not this signal.
    /// </summary>
    IObservable<Unit> CatalogUnauthorized { get; }
}
