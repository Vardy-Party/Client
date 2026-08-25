using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Models;

namespace VardyParty.Services;

public class StreamHealthService(
    HttpClient httpClient,
    IOptions<APISettings> apiSettings,
    ILogger<StreamHealthService> logger) : IStreamHealthService
{
    private readonly string? _baseUrl = apiSettings.Value.HeadlessBaseUrl?.TrimEnd('/') ?? string.Empty;
    private readonly TimeSpan _recommendationsTimeout = TimeSpan.FromSeconds(10);

    public async Task<RecommendationResponse?> GetRecommendationsAsync(
        string league,
        string homeTeam,
        string awayTeam,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/{Uri.EscapeDataString(league)}/{BuildMatchSegment(homeTeam, awayTeam)}/recommendations";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_recommendationsTimeout);
            var response = await httpClient.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();
            var recommendations = await response.Content.ReadFromJsonAsync<RecommendationResponse>(cts.Token);
            logger.LogInformation("[StreamHealth] Recommendations for {League} {Home} vs {Away}: {@Recommendations}",
                league,
                homeTeam,
                awayTeam,
                recommendations);
            return recommendations;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StreamHealth] Failed to fetch recommendations for {League} {Home} vs {Away}",
                league, homeTeam, awayTeam);
            return null;
        }
    }

    public async Task ReportHealthAsync(
        string league,
        string homeTeam,
        string awayTeam,
        StreamHealthReport report,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/{Uri.EscapeDataString(league)}/{BuildMatchSegment(homeTeam, awayTeam)}/health";

        try
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };

            var json = JsonSerializer.Serialize(report, options);

            logger.LogInformation("[StreamHealth] POSTing to {Url}", url);
            logger.LogInformation("[StreamHealth] Request body: {Json}", json);
            logger.LogInformation("[StreamHealth] Request body length: {Length} bytes",
                Encoding.UTF8.GetByteCount(json));

            var content = new StringContent(json, Encoding.UTF8, "application/json");

            logger.LogInformation("[StreamHealth] Content-Type: {ContentType}",
                content.Headers.ContentType?.ToString());

            var response = await httpClient.PostAsync(url, content, cancellationToken);

            logger.LogInformation("[StreamHealth] Response status: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "[StreamHealth] Health report FAILED. Status: {StatusCode}, Response: {ResponseContent}",
                    response.StatusCode,
                    responseContent);
            }
            else
            {
                logger.LogInformation("[StreamHealth] Health report SUCCESS");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StreamHealth] Failed to report stream health");
        }
    }

    public async Task<StreamStatsResponse?> GetStatsAsync(
        string league,
        string homeTeam,
        string awayTeam,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/{Uri.EscapeDataString(league)}/{BuildMatchSegment(homeTeam, awayTeam)}/stats";

        try
        {
            var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<StreamStatsResponse>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StreamHealth] Failed to fetch stats for {League} {Home} vs {Away}", league,
                homeTeam, awayTeam);
            return null;
        }
    }

    private static string BuildMatchSegment(string homeTeam, string awayTeam)
    {
        return $"{Uri.EscapeDataString(homeTeam)}{Uri.EscapeDataString(" v ")}{Uri.EscapeDataString(awayTeam)}";
    }
}