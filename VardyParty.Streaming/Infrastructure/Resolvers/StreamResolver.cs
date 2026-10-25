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

        // Catalog MP rows often have no chip labels yet — LocalService discovers them
        // on the first /mp. Keep overlay total unknown (indeterminate) until then.
        if (!pending.Any(HasUnknownMpChipFanOut))
        {
            onTotalStreamsKnown?.Invoke(pending.Count);
        }

        var concurrency = Math.Max(1, batchSize);
        logger.LogInformation(
            "[StreamResolver] Starting incremental resolution of {Count} streams (concurrency {Concurrency}; yield-as-ready; MP single-flight)",
            pending.Count, concurrency);

        // Yield each finished resolve immediately. Non-MP rows may run in parallel
        // up to `concurrency`, including while one MP is in flight. At most one
        // MP (/mp) is in flight — LocalService is single-threaded for POST /mp.
        // When the next row is another MP, scan forward for a non-MP row instead
        // of stalling the window. First playback still follows catalog index
        // (StreamResolutionOrchestrator holds a faster later row until earlier
        // candidates have yielded). Chip siblings from /mp are appended to
        // `pending` and picked up by FillWindow.
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var inFlight = new List<(Task<ResolveOutcome> Task, bool IsMp)>();
        var started = new HashSet<int>();
        var nextIndex = 0;
        var activeMp = 0;

        async Task<ResolveOutcome> StartAsync(Stream stream)
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
            while (inFlight.Count < concurrency)
            {
                int? pick = null;
                for (var i = nextIndex; i < pending.Count; i++)
                {
                    if (started.Contains(i))
                        continue;

                    if (MpPageUrl.IsV2Stream(pending[i]) && activeMp > 0)
                        continue;

                    pick = i;
                    break;
                }

                if (pick is not int index)
                    break;

                started.Add(index);
                while (nextIndex < pending.Count && started.Contains(nextIndex))
                    nextIndex++;

                var stream = pending[index];
                var isMp = MpPageUrl.IsV2Stream(stream);
                if (isMp)
                    activeMp++;

                logger.LogInformation(
                    "[StreamResolver] Starting resolve {Index}/{Count} for {Channel} ({Kind})",
                    index + 1, pending.Count, stream.Channel, isMp ? "MP" : "FB");
                inFlight.Add((StartAsync(stream), isMp));
            }
        }

        FillWindow();

        while (inFlight.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completedTask = await Task.WhenAny(inFlight.Select(x => x.Task)).ConfigureAwait(false);
            var index = inFlight.FindIndex(x => ReferenceEquals(x.Task, completedTask));
            var entry = inFlight[index];
            inFlight.RemoveAt(index);
            if (entry.IsMp)
                activeMp--;

            var outcome = await entry.Task.ConfigureAwait(false);
            seen.Add(CandidateKey(outcome.Enriched.Stream));

            if (outcome.Siblings.Count > 0)
                EnqueueMpSiblingsNext(pending, started, ref nextIndex, seen, outcome.Siblings);

            // Publish total once MP chip inventory is known (siblings and/or sole chip).
            if (entry.IsMp || outcome.Siblings.Count > 0)
            {
                onTotalStreamsKnown?.Invoke(pending.Count);
            }

            if (outcome.Siblings.Count > 0)
            {
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

            cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                enriched.ErrorMessage = localLanPlayService.UsesRemoteCompute
                    ? localLanPlayService.LastRemoteComputeError ?? "No playlist from the paired computer"
                    : "No m3u8 URL returned from local LAN play service";
                logger.LogWarning("[StreamResolver] Failed to get m3u8 URL for {Channel}: {Error}",
                    stream.Channel, enriched.ErrorMessage);
                return new ResolveOutcome(enriched, SiblingsFrom(stream, m3u8Response));
            }

            V2StreamExpander.ApplySelectedChip(stream, m3u8Response?.SelectedStream);
            enriched.ResolvedM3U8Url = m3u8Url;
            enriched.Status = StreamResolutionStatus.Resolved;
            enriched.RequestHeaders = m3u8Response?.RequestHeaders;
            enriched.RewrittenSegments = m3u8Response?.RewrittenSegments?
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToList();
            enriched.Referer = ResolveReferer(m3u8Response, stream.Url);
            logger.LogInformation("[StreamResolver] Resolved m3u8 for {Channel}: {Url}", stream.Channel, m3u8Url);

            // Step 2: Health — but never burn a one-shot signed m3u8 on a Client
            // CDN probe when LocalService already soaked Referer/Cookie / V2 samples.
            // Probe-then-trust was too late: ExoPlayer gets the second (dead) use.
            StreamHealth health;
            if (LocalServiceSoakTrust.ShouldSkipClientCdnProbe(m3u8Response))
            {
                logger.LogInformation(
                    "[StreamResolver] Skipping Client CDN probe for {Channel} — LocalService soak present (one-shot token)",
                    stream.Channel);
                health = new StreamHealth
                {
                    Url = m3u8Url,
                    Status = StreamHealthStatus.Healthy,
                    ErrorMessage = "Trusted LocalService soak (CDN probe skipped — one-shot token)"
                };
            }
            else
            {
                logger.LogInformation("[StreamResolver] Testing health and extracting metadata for {Channel}",
                    stream.Channel);
                logger.LogInformation("[StreamResolver] Using referer for health check: {Referer}", enriched.Referer);
                var probe = TryLocalServiceHealthProbe(m3u8Response);
                health = probe is null
                    ? await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, cancellationToken)
                    : await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, probe, cancellationToken);

                if (health.Status != StreamHealthStatus.Healthy
                    && LocalServiceSoakTrust.ShouldTrust(m3u8Response, health.Status))
                {
                    logger.LogInformation(
                        "[StreamResolver] Trusting LocalService soak for {Channel} after Client CDN probe {Status}",
                        stream.Channel, health.Status);
                    health = new StreamHealth
                    {
                        Url = m3u8Url,
                        Status = StreamHealthStatus.Healthy,
                        ErrorMessage = $"Trusted LocalService soak (Client probe was {health.Status})",
                        CheckDurationMs = health.CheckDurationMs,
                        Resolution = health.Resolution,
                        FrameRate = health.FrameRate,
                        Bitrate = health.Bitrate,
                        VideoCodec = health.VideoCodec,
                        AudioCodec = health.AudioCodec
                    };
                }
            }

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
                // and LocalService did not leave soak evidence (cookies / rewritten segments).
                enriched.Status = StreamResolutionStatus.Failed;
                enriched.ErrorMessage = $"Health check failed: {health.Status}";
                logger.LogWarning("[StreamResolver] Stream {Channel} failed health check: {Status}",
                    stream.Channel, health.Status);
            }

            return new ResolveOutcome(enriched, SiblingsFrom(stream, m3u8Response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private static readonly HashSet<string> LocalServicePlaybackHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "user-agent",
        "referer",
        "origin",
        "cookie",
        "authorization"
    };

    /// <summary>
    /// Build a health probe from LocalService soak data: rewritten MP segments
    /// and/or captured request headers (Cookie) from V1/FB <c>/play</c>.
    /// </summary>
    private static StreamHealthProbe? TryLocalServiceHealthProbe(M3U8Response? response)
    {
        if (response is null)
            return null;

        var rewritten = response.RewrittenSegments?
            .Where(MpRewrittenSegment.LooksLike)
            .ToList();
        var headers = FilterPlaybackHeaders(response.RequestHeaders);
        var hasRewritten = rewritten is { Count: > 0 };
        var hasHeaders = headers is { Count: > 0 };
        if (!hasRewritten && !hasHeaders)
            return null;

        return new StreamHealthProbe
        {
            RewrittenSegmentUrls = hasRewritten ? rewritten : null,
            RequestHeaders = hasHeaders ? headers : null
        };
    }

    private static Dictionary<string, string>? FilterPlaybackHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

        Dictionary<string, string>? filtered = null;
        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)
                || !LocalServicePlaybackHeaderNames.Contains(pair.Key))
            {
                continue;
            }

            filtered ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            filtered[pair.Key] = pair.Value;
        }

        return filtered;
    }

    /// <summary>
    /// Insert newly discovered MP chip siblings ahead of remaining catalog work
    /// so harvested labels resolve before later FB rows.
    /// </summary>
    private static void EnqueueMpSiblingsNext(
        List<Stream> pending,
        HashSet<int> started,
        ref int nextIndex,
        HashSet<string> seen,
        IReadOnlyList<Stream> siblings)
    {
        var insertAt = 0;
        while (insertAt < pending.Count && started.Contains(insertAt))
            insertAt++;

        var firstInserted = -1;
        foreach (var sibling in siblings)
        {
            if (!seen.Add(CandidateKey(sibling)))
                continue;

            pending.Insert(insertAt, sibling);
            if (firstInserted < 0)
                firstInserted = insertAt;

            var shifted = new HashSet<int>();
            foreach (var index in started)
                shifted.Add(index >= insertAt ? index + 1 : index);
            started.Clear();
            foreach (var index in shifted)
                started.Add(index);

            insertAt++;
        }

        if (firstInserted >= 0)
            nextIndex = firstInserted;
    }

    private static bool HasUnknownMpChipFanOut(Stream stream) =>
        MpPageUrl.IsV2Stream(stream)
        && string.IsNullOrWhiteSpace(stream.PlayerStream)
        && (stream.PlayerStreams is null || stream.PlayerStreams.Count == 0)
        && string.IsNullOrWhiteSpace(stream.Channel);

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
        if (StreamCandidateRules.ShouldSkipCountdown(stream.IsCountdown))
        {
            return null;
        }

        // MP/v2: pass chip label when known. Catalog often has source=mp without resolutionStrategy=v2.
        if (!stream.RequiresV2StreamSelection
            && !string.Equals(stream.Source, "mp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream.ResolveCatalogSource(), "mp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(stream.PlayerStream))
        {
            return stream.PlayerStream.Trim();
        }

        if (!string.IsNullOrWhiteSpace(stream.Channel)
            && !string.Equals(stream.Channel, "V2", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream.Channel, "MP", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream.Channel, "FB", StringComparison.OrdinalIgnoreCase))
        {
            return stream.Channel.Trim();
        }

        return null;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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