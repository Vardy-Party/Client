using VardyParty.Models;

namespace VardyParty.Streaming;

public interface IStreamHealthService
{
    Task<RecommendationResponse?> GetRecommendationsAsync(
        string league,
        string homeTeam,
        string awayTeam,
        CancellationToken cancellationToken = default);

    Task ReportHealthAsync(
        string league,
        string homeTeam,
        string awayTeam,
        StreamHealthReport report,
        CancellationToken cancellationToken = default);

    Task<StreamStatsResponse?> GetStatsAsync(
        string league,
        string homeTeam,
        string awayTeam,
        CancellationToken cancellationToken = default);
}