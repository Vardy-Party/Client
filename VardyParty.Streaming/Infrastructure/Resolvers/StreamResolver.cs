using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using Stream = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

public class StreamResolver(
    IStreamHealthChecker healthChecker,
    ILocalLanPlayService localLanPlayService,
    IDiscoveredChipNormalizer chipNormalizer,
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

        var pending = streams.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in pending)
        {
            seen.Add(CandidateKey(stream));
        }

        onTotalStreamsKnown?.Invoke(pending.Count);

        logger.LogInformation(
            "[StreamResolver] Starting incremental resolution of {Count} streams in batches of {BatchSize}",
            pending.Count, batchSize);

        var offset = 0;
        var batchNumber = 0;
        while (offset < pending.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = pending.Skip(offset).Take(batchSize).ToList();
            offset += batch.Count;
            batchNumber++;

            logger.LogInformation("[StreamResolver] Processing batch {BatchNumber} with {Count} streams",
                batchNumber, batch.Count);

            // /mp is single-threaded on LocalService. Parallel POSTs just burn the 60s client timeout.
            var results = await ResolveBatchAsync(batch, cancellationToken);

            foreach (var outcome in results)
            {
                seen.Add(CandidateKey(outcome.Enriched.Stream));

                foreach (var sibling in outcome.Siblings)
                {
                    if (!seen.Add(CandidateKey(sibling)))
                    {
                        continue;
                    }

                    pending.Add(sibling);
                }

                if (outcome.Siblings.Count > 0)
                {
                    onTotalStreamsKnown?.Invoke(pending.Count);
                    logger.LogInformation(
                        "[StreamResolver] LocalService listed {Extra} more MP chips ({Chips}) for {Url}; total now {Total}",
                        outcome.Siblings.Count,
                        string.Join(", ", outcome.Siblings.Select(s => s.PlayerStream)),
                        outcome.Enriched.Stream.Url,
                        pending.Count);
                }

                logger.LogInformation("[StreamResolver] Yielding resolved stream: {Channel} ({Status})",
                    outcome.Enriched.Stream.Channel, outcome.Enriched.Status);
                yield return outcome.Enriched;
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

    private readonly record struct ResolveOutcome(EnrichedStream Enriched, IReadOnlyList<Stream> Siblings);

    private static string CandidateKey(Stream stream) =>
        $"{stream.Url}\n{stream.PlayerStream?.Trim() ?? ""}";

    private async Task<ResolveOutcome[]> ResolveBatchAsync(
        List<Stream> batch,
        CancellationToken cancellationToken)
    {
        var outcomes = new ResolveOutcome[batch.Count];
        var parallel = new List<(int Index, Stream Stream)>();
        var sequentialMp = new List<(int Index, Stream Stream)>();
        for (var i = 0; i < batch.Count; i++)
        {
            var item = (i, batch[i]);
            if (MpPageUrl.IsV2Stream(batch[i]))
            {
                sequentialMp.Add(item);
            }
            else
            {
                parallel.Add(item);
            }
        }

        if (parallel.Count > 0)
        {
            await Task.WhenAll(parallel.Select(async item =>
            {
                outcomes[item.Index] = await ResolveAndTestStreamAsync(item.Stream, cancellationToken);
            }));
        }

        foreach (var item in sequentialMp)
        {
            outcomes[item.Index] = await ResolveAndTestStreamAsync(item.Stream, cancellationToken);
        }

        return outcomes;
    }

    private Task<ResolveOutcome> ResolveAndTestStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        return ResolveAndTestStreamInternalAsync(stream, cancellationToken);
    }

    private async Task<ResolveOutcome> ResolveAndTestStreamInternalAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        var enriched = new EnrichedStream { Stream = stream, Status = StreamResolutionStatus.Pending };

        if (StreamCandidateRules.ShouldSkipCountdown(stream.IsCountdown))
        {
            enriched.Status = StreamResolutionStatus.Failed;
            enriched.ErrorMessage = "Stream page is in countdown (not started yet)";
            logger.LogInformation("[StreamResolver] Skipping {Channel}: countdown active on stream page", stream.Channel);
            return new ResolveOutcome(enriched, []);
        }

        try
        {
            // Step 1: Resolve m3u8 URL
            logger.LogInformation("[StreamResolver] Resolving m3u8 for {Channel}", stream.Channel);
            var m3u8Response = await ResolveM3U8ResponseInternalAsync(stream, cancellationToken);
            var m3u8Url = m3u8Response?.Url;

            if (string.IsNullOrEmpty(m3u8Url))
            {
                V2StreamExpander.ApplySelectedChip(stream, m3u8Response?.SelectedStream);
                enriched.Status = StreamResolutionStatus.Failed;
                enriched.ErrorMessage = "No m3u8 URL returned from local LAN play service";
                logger.LogWarning("[StreamResolver] Failed to get m3u8 URL for {Channel}: {Error}",
                    stream.Channel, enriched.ErrorMessage);
                return new ResolveOutcome(enriched, SiblingsFrom(stream, m3u8Response));
            }

            V2StreamExpander.ApplySelectedChip(stream, m3u8Response?.SelectedStream);
            enriched.ResolvedM3U8Url = m3u8Url;
            enriched.Status = StreamResolutionStatus.Resolved;
            enriched.RequestHeaders = m3u8Response?.RequestHeaders;
            enriched.Referer = ResolveReferer(m3u8Response, stream.Url);
            logger.LogInformation("[StreamResolver] Resolved m3u8 for {Channel}: {Url}", stream.Channel, m3u8Url);

            // Step 2: Test health and extract metadata
            logger.LogInformation("[StreamResolver] Testing health and extracting metadata for {Channel}",
                stream.Channel);
            logger.LogInformation("[StreamResolver] Using referer for health check: {Referer}", enriched.Referer);
            var probe = TryMpHealthProbe(m3u8Response);
            var health = probe is null
                ? await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, cancellationToken)
                : await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, probe, cancellationToken);
            enriched.Health = health;
            if (health.Status == StreamHealthStatus.Healthy)
            {
                enriched.Status = StreamResolutionStatus.Healthy;
                logger.LogInformation("[StreamResolver] Stream {Channel} is healthy: {Quality}",
                    stream.Channel, health.GetQualityLabel());
            }
            else
            {
                // ManifestUnreachable / SegmentUnreachable both mean the CDN refused the probe connection —
                // the same URL will fail in ExoPlayer. Do not trust it for playback.
                enriched.Status = StreamResolutionStatus.Failed;
                enriched.ErrorMessage = $"Health check failed: {health.Status}";
                logger.LogWarning("[StreamResolver] Stream {Channel} failed health check: {Status}",
                    stream.Channel, health.Status);
            }

            return new ResolveOutcome(enriched, SiblingsFrom(stream, m3u8Response));
        }
        catch (Exception ex)
        {
            enriched.Status = StreamResolutionStatus.Failed;
            enriched.ErrorMessage = ex.Message;
            logger.LogError(ex, "[StreamResolver] Failed to resolve m3u8 URL for testing for {Channel} with {url}",
                stream.Channel, stream.Url);
            return new ResolveOutcome(enriched, []);
        }
    }

    private static StreamHealthProbe? TryMpHealthProbe(M3U8Response? response)
    {
        var rewritten = response?.RewrittenSegments?
            .Where(url => !string.IsNullOrWhiteSpace(url) && LooksLikeRewrittenSegment(url))
            .ToList();
        if (rewritten is not { Count: > 0 })
        {
            return null;
        }

        return new StreamHealthProbe
        {
            RewrittenSegmentUrls = rewritten,
            RequestHeaders = response!.RequestHeaders
        };
    }

    private static bool LooksLikeRewrittenSegment(string url) =>
        url.Contains("_s2=", StringComparison.OrdinalIgnoreCase)
        || url.Contains("kdns.fr", StringComparison.OrdinalIgnoreCase)
        || url.Contains("fhlsport", StringComparison.OrdinalIgnoreCase)
        || url.Contains("isyjvux", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<Stream> SiblingsFrom(Stream source, M3U8Response? response)
    {
        if (response is null || !MpPageUrl.IsV2Stream(source))
        {
            return [];
        }

        return V2StreamExpander.RemainingFromLocalService(
            source,
            response.Streams,
            response.SelectedStream,
            chipNormalizer);
    }

    private static string? GetPlayerStreamName(Stream stream)
    {
        if (!stream.RequiresV2StreamSelection || StreamCandidateRules.ShouldSkipCountdown(stream.IsCountdown))
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
        var response = await ResolveM3U8ResponseInternalAsync(stream, cancellationToken);
        return response?.Url;
    }

    private async Task<M3U8Response?> ResolveM3U8ResponseInternalAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var playerStreamName = GetPlayerStreamName(stream);
            logger.LogInformation(
                "[StreamResolver] Fetching M3U8 from local LAN service for source {Url}{StreamSuffix}",
                stream.Url,
                playerStreamName is null ? "" : $" (stream={playerStreamName})");
            var result = await localLanPlayService.ResolveM3U8UrlAsync(
                stream.Url,
                playerStreamName,
                stream.ResolutionStrategy,
                stream.Source,
                cancellationToken);
            logger.LogInformation("[StreamResolver] M3U8 resolve completed for source {Url}", stream.Url);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StreamResolver] Failed to fetch M3U8 for {Url}", stream.Url);
            return null;
        }
    }

    private static string ResolveReferer(M3U8Response? response, string fallbackUrl)
    {
        var capturedReferer = GetHeaderValue(response?.RequestHeaders, "referer");
        return string.IsNullOrWhiteSpace(capturedReferer) ? fallbackUrl : capturedReferer;
    }

    private static string? GetHeaderValue(IReadOnlyDictionary<string, string>? headers, string headerName)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        foreach (var pair in headers)
        {
            if (pair.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }
}