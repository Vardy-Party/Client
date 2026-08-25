using VardyParty.Models;

namespace VardyParty.Services;

/// <summary>
/// Catalog-facing games list. Streaming's <c>IApiService</c> implements this so
/// Catalog never depends on stream/M3U8 HTTP.
/// </summary>
public interface IGamesCatalogApi
{
    Task<Dictionary<string, List<Game>>> GetAllGamesAsync(bool forceRefresh = false);
}
