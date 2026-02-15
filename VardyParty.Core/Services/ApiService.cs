using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Exceptions;
using VardyParty.Models;
using VardyParty.Resolvers;
using Stream = VardyParty.Models.Stream;

namespace VardyParty.Services;

public class ApiService(
    HttpClient httpClient,
    ILogger<ApiService> logger,
    IStreamResolver streamResolver,
    IStreamDeduplicator streamDeduplicator,
    IOptions<GamesApiSettings> gamesApiSettings,
    IOptions<APISettings> apiSettings) : IApiService
{
    private readonly string? _baseUrl = $"{apiSettings.Value.HeadlessBaseUrl.TrimEnd('/')}/";
    private readonly TimeSpan _callTimeout = TimeSpan.FromSeconds(gamesApiSettings.Value?.CallTimeoutSeconds ?? 45);

    private readonly TimeSpan _m3u8CallTimeout =
        TimeSpan.FromSeconds(gamesApiSettings.Value?.M3U8CallTimeoutSeconds ?? 10);

    private readonly int _maxRetries = gamesApiSettings.Value?.MaxRetries ?? 2;

    public async Task<StreamResponse?> GetStreamsAsync(string league, string homeTeam, string awayTeam,
        bool forceRefresh = false)
    {
        try
        {
            var url =
                $"{_baseUrl}/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(homeTeam)}{Uri.EscapeDataString(" v ")}{Uri.EscapeDataString(awayTeam)}";
            var response = await FetchWithRetriesAsync<StreamResponse>(url);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching streams for {Home} vs {Away}", homeTeam, awayTeam);
            return null;
        }
    }

    public async Task<M3U8Response?> GetM3U8UrlAsync(string streamUrl)
    {
        var url = $"{_baseUrl}/play/{Uri.EscapeDataString(streamUrl)}";
        try
        {
            using var cts = new CancellationTokenSource(_m3u8CallTimeout);
            logger.LogInformation("[Api] Fetching M3U8 From {Url}", streamUrl);
            var response = await httpClient.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<M3U8Response>(cts.Token);
            logger.LogInformation("[Api] M3U8 fetched for {Url}", streamUrl);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Api] Failed to fetch M3U8 for {Url}", streamUrl);
            return null;
        }
    }

    public async Task<string?> ResolveM3U8ForPlaybackAsync(
        Stream stream,
        string refererUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("[Api] Resolving m3u8 for playback: {Channel}", stream.Channel);
            var m3u8Response = await GetM3U8UrlAsync(stream.Url);

            if (m3u8Response != null && !string.IsNullOrEmpty(m3u8Response.Url))
            {
                logger.LogInformation("[Api] Successfully resolved m3u8 for playback: {Channel}", stream.Channel);
                return m3u8Response.Url;
            }

            logger.LogWarning("[Api] No m3u8 URL returned for playback resolution: {Channel}", stream.Channel);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Api] Failed to resolve m3u8 for playback: {Channel}", stream.Channel);
            return null;
        }
    }

    public async Task<Dictionary<string, List<Game>>> GetAllGamesAsync(bool forceRefresh = false)
    {
        try
        {
            var url = $"{_baseUrl}/new";
            logger.LogInformation("[Api] GetAllGamesAsync fetching from {Url}", url);
            var attempt = 0;
            var delay = TimeSpan.FromSeconds(1);
            Exception? lastException = null;

            while (true)
            {
                attempt++;
                try
                {
                    using var cts = new CancellationTokenSource(_callTimeout);
                    var response = await httpClient.GetAsync(url, cts.Token);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(cts.Token);
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, List<Game>>>(json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }) ?? new Dictionary<string, List<Game>>();

                    NormalizeGames(parsed);

                    var total = parsed.Values.Sum(list => list?.Count ?? 0);
                    logger.LogInformation("[Api] Fetched {Count} games", total);
                    return parsed;
                }
                catch (HttpRequestException ex) when (attempt <= _maxRetries &&
                                                      ex.StatusCode == HttpStatusCode.InternalServerError)
                {
                    logger.LogWarning(ex, "[Api] Attempt {Attempt} failed with HTTP 500, retrying...", attempt);
                    lastException = ex;
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.InternalServerError)
                {
                    logger.LogError(ex, "[Api] Failed to fetch games after {Attempt} attempts - HTTP 500", attempt);
                    throw new ApiSystemDownException("Games API returned HTTP 500 after all retries", ex);
                }
                catch (Exception ex) when (attempt <= _maxRetries)
                {
                    logger.LogWarning(ex, "[Api] Attempt {Attempt} failed, retrying...", attempt);
                    lastException = ex;
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Api] Failed to fetch games");
                    return new Dictionary<string, List<Game>>();
                }
            }
        }
        catch (ApiSystemDownException)
        {
            // Re-throw to allow caller to handle
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Api] GetAllGamesAsync error");
            return new Dictionary<string, List<Game>>();
        }
    }

    public async IAsyncEnumerable<EnrichedStream> GetEnrichedStreamsAsync(
        string league,
        string homeTeam,
        string awayTeam,
        Action<int>? onTotalStreamsKnown = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Get initial streams from API
        StreamResponse? response = null;
        try
        {
            response = await GetStreamsAsync(league, homeTeam, awayTeam);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Api] Error fetching initial streams for {Home} vs {Away}", homeTeam, awayTeam);
            yield break;
        }

        if (response?.Streams == null || response.Streams.Count == 0)
        {
            logger.LogInformation("[Api] No streams found for {Home} vs {Away}", homeTeam, awayTeam);
            yield break;
        }

        // Deduplicate streams by base m3u8 URL
        List<Stream> deduplicated;
        try
        {
            logger.LogInformation("[Api] Deduplicating {Count} streams", response.Streams.Count);
            deduplicated = streamDeduplicator.DeduplicateStreams(response.Streams);
            logger.LogInformation("[Api] Starting incremental resolution of {Count} deduplicated streams",
                deduplicated.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Api] Error deduplicating streams for {Home} vs {Away}", homeTeam, awayTeam);
            yield break;
        }

        try
        {
            await foreach (var enrichedStream in streamResolver.ResolveStreamsIncrementallyAsync(
                               deduplicated,
                               onTotalStreamsKnown: onTotalStreamsKnown,
                               cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return enrichedStream;
            }
        }
        finally
        {
            logger.LogInformation("[Api] Completed enriched streams resolution for {Home} vs {Away}", homeTeam,
                awayTeam);
        }
    }

    private async Task<T?> FetchWithRetriesAsync<T>(string url) where T : class
    {
        var attempt = 0;
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(_callTimeout);
                var response = await httpClient.GetAsync(url, cts.Token);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Do not retry on 401 - credentials/authorization issue needs user action
                    logger.LogWarning("[Api] Received 401 Unauthorized for {Url} - aborting without retry", url);
                    return null;
                }

                if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>(cts.Token);

                // For non-success status codes, throw to be handled by retry logic below
                throw new HttpRequestException($"Request failed with status code {response.StatusCode}", null,
                    response.StatusCode);
            }
            catch (Exception ex) when (attempt <= _maxRetries)
            {
                // If exception was due to 401, do not retry (HttpRequestException may carry StatusCode)
                if (ex is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.Unauthorized)
                {
                    logger.LogWarning(ex, "[Api] Received 401 Unauthorized for {Url} - aborting without retry", url);
                    return null;
                }

                logger.LogWarning(ex, "[Api] Attempt {Attempt} failed for {Url}, retrying...", attempt, url);
                await Task.Delay(delay);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Api] Failed to fetch {Url} after {Attempt} attempts", url, attempt);
                return null;
            }
        }
    }

    private void NormalizeGames(Dictionary<string, List<Game>> dict)
    {
        foreach (var kvp in dict)
        {
            var leagueKey = kvp.Key;
            if (kvp.Value == null) continue;
            foreach (var g in kvp.Value)
            {
                if (string.IsNullOrEmpty(g.ApiLeague)) g.ApiLeague = leagueKey;
                if (string.IsNullOrEmpty(g.League)) g.League = leagueKey;
                if (g.Start.Kind != DateTimeKind.Utc) g.Start = g.Start.ToUniversalTime();
            }
        }
    }
}