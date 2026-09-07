using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using Stream = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

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

        var concurrency = Math.Max(1, batchSize);
        logger.LogInformation(
            "[StreamResolver] Starting incremental resolution of {Count} streams with concurrency {Concurrency}",
            streams.Count, concurrency);

        // Keep up to `concurrency` resolves in flight. Yield each result as it
        // finishes so the UI can count 1, 2, 3… instead of waiting on a pair.
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var inFlight = new List<Task<EnrichedStream>>();
        var nextIndex = 0;

        async Task<EnrichedStream> StartAsync(Stream stream)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ResolveAndTestStreamAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        void FillWindow()
        {
            while (nextIndex < streams.Count && inFlight.Count < concurrency)
            {
                var stream = streams[nextIndex++];
                logger.LogInformation(
                    "[StreamResolver] Starting resolve {Index}/{Count} for {Channel}",
                    nextIndex, streams.Count, stream.Channel);
                inFlight.Add(StartAsync(stream));
            }
        }

        FillWindow();

        while (inFlight.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completed = await Task.WhenAny(inFlight).ConfigureAwait(false);
            inFlight.Remove(completed);
            var enriched = await completed.ConfigureAwait(false);
            logger.LogInformation("[StreamResolver] Yielding resolved stream: {Channel} ({Status})",
                enriched.Stream.Channel, enriched.Status);
            yield return enriched;
            FillWindow();
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

        if (StreamCandidateRules.ShouldSkipCountdown(stream.IsCountdown))
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
            var m3u8Response = await ResolveM3U8ResponseInternalAsync(stream, cancellationToken);
            var m3u8Url = m3u8Response?.Url;

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
            enriched.RequestHeaders = m3u8Response?.RequestHeaders;
            enriched.Referer = ResolveReferer(m3u8Response, stream.Url);
            logger.LogInformation("[StreamResolver] Resolved m3u8 for {Channel}: {Url}", stream.Channel, m3u8Url);

            // Step 2: Test health and extract metadata
            logger.LogInformation("[StreamResolver] Testing health and extracting metadata for {Channel}",
                stream.Channel);
            logger.LogInformation("[StreamResolver] Using referer for health check: {Referer}", enriched.Referer);
            var health = await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, cancellationToken);
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
            var result = await localLanPlayService.ResolveM3U8UrlAsync(stream.Url, playerStreamName, cancellationToken);
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