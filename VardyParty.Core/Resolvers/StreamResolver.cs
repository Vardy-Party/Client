using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Services;
using Stream = VardyParty.Models.Stream;

namespace VardyParty.Resolvers;

public class StreamResolver(
    IStreamHealthChecker healthChecker,
    ILocalLanPlayService localLanPlayService,
    ILogger<StreamResolver> logger) : IStreamResolver
{
    public async IAsyncEnumerable<EnrichedStream> ResolveStreamsIncrementallyAsync(
        List<Stream> streams,
        int batchSize = 3,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<int>? onTotalStreamsKnown = null)
    {
        if (streams == null || streams.Count == 0)
        {
            logger.LogInformation("[StreamResolver] No streams to resolve");
            yield break;
        }

        // Report the total stream count upfront
        onTotalStreamsKnown?.Invoke(streams.Count);

        logger.LogInformation(
            "[StreamResolver] Starting incremental resolution of {Count} streams in batches of {BatchSize}",
            streams.Count, batchSize);

        // Process streams in batches
        for (var i = 0; i < streams.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = streams
                .Skip(i)
                .Take(batchSize)
                .ToList();

            logger.LogInformation("[StreamResolver] Processing batch {BatchNumber} with {Count} streams",
                i / batchSize + 1, batch.Count);

            // Resolve and test each stream in the batch in parallel
            var tasks = batch.Select(stream => ResolveAndTestStreamAsync(stream, cancellationToken)).ToList();

            // As each completes, yield it immediately (not waiting for whole batch)
            var completed = new HashSet<Task>();
            while (completed.Count < tasks.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var nextCompleted = await Task.WhenAny(tasks.Except(completed).ToList());
                completed.Add(nextCompleted);

                // Get the result and yield it
                if (nextCompleted is Task<EnrichedStream> task)
                {
                    var enriched = await task;
                    logger.LogInformation("[StreamResolver] Yielding resolved stream: {Channel} ({Status})",
                        enriched.Stream.Channel, enriched.Status);
                    yield return enriched;
                }
            }
        }

        logger.LogInformation("[StreamResolver] Completed incremental resolution of all streams");
    }

    public async Task<string?> ResolveM3U8UrlAsync(
        Stream stream,
        string refererUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("[StreamResolver] Resolving m3u8 URL for single stream: {Channel}", stream.Channel);

            var m3u8Url = await GetM3U8UrlInternalAsync(stream, cancellationToken);

            if (!string.IsNullOrEmpty(m3u8Url))
            {
                logger.LogInformation("[StreamResolver] Successfully resolved m3u8 URL for {Channel}", stream.Channel);
                return m3u8Url;
            }

            logger.LogWarning("[StreamResolver] No m3u8 URL returned for {Channel}", stream.Channel);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StreamResolver] Failed to resolve m3u8 URL for {Channel} with {url}", stream.Channel,
                stream.Url);
            return null;
        }
    }

    private Task<EnrichedStream> ResolveAndTestStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        return ResolveAndTestStreamInternalAsync(stream, cancellationToken);
    }

    private async Task<EnrichedStream> ResolveAndTestStreamInternalAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        var enriched = new EnrichedStream { Stream = stream, Status = StreamResolutionStatus.Pending };

        if (stream.IsCountdown)
        {
            enriched.Status = StreamResolutionStatus.Failed;
            enriched.ErrorMessage = "Stream page is in countdown (not started yet)";
            logger.LogInformation("[StreamResolver] Skipping {Channel}: countdown active on stream page", stream.Channel);
            return enriched;
        }

        try
        {
            // Step 1: Resolve m3u8 URL
            logger.LogInformation("[StreamResolver] Resolving m3u8 for {Channel}", stream.Channel);
            var m3u8Url = await GetM3U8UrlInternalAsync(stream, cancellationToken);

            if (string.IsNullOrEmpty(m3u8Url))
            {
                enriched.Status = StreamResolutionStatus.Failed;
                enriched.ErrorMessage = "No m3u8 URL returned from local LAN play service";
                logger.LogWarning("[StreamResolver] Failed to get m3u8 URL for {Channel}: {Error}",
                    stream.Channel, enriched.ErrorMessage);
                return enriched;
            }

            enriched.ResolvedM3U8Url = m3u8Url;
            enriched.Status = StreamResolutionStatus.Resolved;
            enriched.Referer = stream.Url;
            logger.LogInformation("[StreamResolver] Resolved m3u8 for {Channel}: {Url}", stream.Channel, m3u8Url);

            // Step 2: Test health and extract metadata
            logger.LogInformation("[StreamResolver] Testing health and extracting metadata for {Channel}",
                stream.Channel);
            logger.LogInformation("[StreamResolver] Using referer for health check: {Referer}", enriched.Referer);
            var health = await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, cancellationToken);
            enriched.Health = health;
            enriched.Status = health.Status == StreamHealthStatus.Healthy
                ? StreamResolutionStatus.Healthy
                : StreamResolutionStatus.Failed;

            if (enriched.Status == StreamResolutionStatus.Healthy)
            {
                logger.LogInformation("[StreamResolver] Stream {Channel} is healthy: {Quality}",
                    stream.Channel, health.GetQualityLabel());
            }
            else
            {
                enriched.ErrorMessage = $"Health check failed: {health.Status}";
                logger.LogWarning("[StreamResolver] Stream {Channel} failed health check: {Status}",
                    stream.Channel, health.Status);
            }

            return enriched;
        }
        catch (Exception ex)
        {
            enriched.Status = StreamResolutionStatus.Failed;
            enriched.ErrorMessage = ex.Message;
            logger.LogError(ex, "[StreamResolver] Failed to resolve m3u8 URL for testing for {Channel} with {url}",
                stream.Channel, stream.Url);
            return enriched;
        }
    }

    private static string? GetPlayerStreamName(Stream stream)
    {
        if (!stream.RequiresV2StreamSelection || stream.IsCountdown)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(stream.PlayerStream))
        {
            return stream.PlayerStream.Trim();
        }

        return string.IsNullOrWhiteSpace(stream.Channel) ? null : stream.Channel.Trim();
    }

    private async Task<string?> GetM3U8UrlInternalAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var playerStreamName = GetPlayerStreamName(stream);
            logger.LogInformation(
                "[StreamResolver] Fetching M3U8 from local LAN service for source {Url}{StreamSuffix}",
                stream.Url,
                playerStreamName is null ? "" : $" (stream={playerStreamName})");
            var result = await localLanPlayService.ResolveM3U8UrlAsync(stream.Url, playerStreamName, cancellationToken);
            logger.LogInformation("[StreamResolver] M3U8 resolve completed for source {Url}", stream.Url);
            return result?.Url;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StreamResolver] Failed to fetch M3U8 for {Url}", stream.Url);
            return null;
        }
    }
}