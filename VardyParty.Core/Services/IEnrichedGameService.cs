using VardyParty.Models;

namespace VardyParty.Services;

public interface IEnrichedGameService
{
    // Live stream of updates
    IObservable<Dictionary<string, List<Game>>?> GamesStream { get; }
    
    // Stream of error messages (null when no error)
    IObservable<string?> ErrorStream { get; }
}
