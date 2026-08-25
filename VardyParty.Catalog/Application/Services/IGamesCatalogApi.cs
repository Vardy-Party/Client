using VardyParty.Kernel;

namespace VardyParty.Catalog;

/// <summary>
/// Catalog-facing games list. Hosting binds this to the same HTTP implementation
/// as stream/M3U8 calls without putting those methods on this contract.
/// </summary>
public interface IGamesCatalogApi
{
    Task<Dictionary<string, List<Game>>> GetAllGamesAsync(bool forceRefresh = false);
}
