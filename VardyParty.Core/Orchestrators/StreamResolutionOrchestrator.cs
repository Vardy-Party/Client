using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Providers;
using VardyParty.Services;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Orchestrators;

public class StreamResolutionOrchestrator(
    IApiService apiService,
    IStreamResolver streamResolver,
    IStreamSelectionCoordinator selectionCoordinator,
    IStreamHealthService streamHealthService,
    IStreamHealthReporter streamHealthReporter,
    ISessionIdProvider sessionIdProvider,
    SelectionState selectionState,
    IStreamSwitchingService streamSwitchingService,
    INativeVideoPlayerService nativeVideoPlayer,
    ILogger<StreamResolutionOrchestrator> logger) : IStreamResolutionOrchestrator
{
    private static readonly TimeSpan PlaybackHealthInterval = TimeSpan.FromSeconds(30);

    private readonly BehaviorSubject<StreamResolutionProgress> _progressSubject =
        new(new StreamResolutionProgress());

    private bool _hasBufferingOccurred;
    private int _healthyStreamCount;
    private bool _isResolving;
    private string _status = string.Empty;
    private int _streamsTested;

    private int _totalStreams;

    public IObservable<StreamResolutionProgress> ProgressUpdated => _progressSubject;

    public async Task<StreamResolutionOutcome> StartAsync(Game game, CancellationToken cancellationToken = default)
    {
        Reset();
        streamSwitchingService.Initialize(game.ApiLeague, game.Home, game.Away);

        _isResolving = true;
        _status = "Searching for streams...";
        PublishProgress();

        var outcome = new StreamResolutionOutcome();
        var hasPlayedFirstStream = false;
        var streamCount = 0;
        Task<PlaybackResult?>? playbackTask = null;

        await selectionCoordinator.InitializeAsync(game, cancellationToken);
        var orderedCandidates = selectionCoordinator.GetOrderedCandidates();
        var orderedStreams = orderedCandidates.Select(c => c.Stream).ToList();
        if (orderedStreams.Count == 0)
        {
            _status = "No streams found";
            outcome.NoWorkingStreams = true;
            _isResolving = false;
            PublishProgress();
            return outcome;
        }

        await foreach (var enrichedStream in streamResolver.ResolveStreamsIncrementallyAsync(
                           orderedStreams,
                           2,
                           cancellationToken,
                           totalCount =>
                           {
                               _totalStreams = totalCount;
                               PublishProgress();
                           }))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if playback was closed by user
            if (playbackTask != null && playbackTask.IsCompleted)
            {
                var playbackResult = await playbackTask;
                if (playbackResult?.Message?.Contains("User closed", StringComparison.OrdinalIgnoreCase) == true)
                {
                    outcome.UserClosed = true;
                    outcome.PlaybackResult = playbackResult;
                    return outcome;
                }
            }

            streamCount++;
            _streamsTested++;
            PublishProgress();

            if (enrichedStream.Status == StreamResolutionStatus.Failed)
            {
                _ = ReportHealthAsync(game, enrichedStream.Stream, "failed", enrichedStream.ErrorMessage,
                    enrichedStream.Health, cancellationToken);
            }
            else if (enrichedStream.Status == StreamResolutionStatus.Healthy)
            {
                _ = ReportHealthAsync(game, enrichedStream.Stream, "unknown", null, enrichedStream.Health,
                    cancellationToken);
            }

            if (enrichedStream.Status != StreamResolutionStatus.Healthy)
            {
                continue;
            }

            streamSwitchingService.AddHealthyStream(enrichedStream);
            _healthyStreamCount = streamSwitchingService.GetHealthyStreams().Count;
            PublishProgress();

            if (!hasPlayedFirstStream)
            {
                hasPlayedFirstStream = true;
                // Start playback without awaiting so stream testing can continue
                playbackTask = PlayStreamAsync(game, enrichedStream, cancellationToken);
            }
        }

        // Wait for playback to complete if it was started
        if (playbackTask != null)
        {
            var playbackResult = await playbackTask;
            outcome.PlaybackResult = playbackResult;

            if (playbackResult?.Message?.Contains("User closed", StringComparison.OrdinalIgnoreCase) == true)
            {
                outcome.UserClosed = true;
            }
            else if (playbackResult != null && !playbackResult.Success)
            {
                var current = streamSwitchingService.GetCurrentStream();
                if (current == null)
                {
                    // Native session already drained the pool; resume candidate testing.
                    selectionCoordinator.ResumeTesting();
                    _status = "Testing resumed";
                    PublishProgress();
                }
                else
                {
                    await HandlePlaybackFailureAsync(game, current, playbackResult, cancellationToken);
                }
            }
        }

        if (_healthyStreamCount == 0)
        {
            _status = "No working streams found";
            outcome.NoWorkingStreams = true;
            PublishProgress();
        }

        _isResolving = false;
        PublishProgress();
        logger.LogInformation("[StreamResolution] Completed. Total={Total}, Tested={Tested}, Healthy={Healthy}",
            streamCount, _streamsTested, _healthyStreamCount);
        return outcome;
    }

    public void Reset()
    {
        _totalStreams = 0;
        _streamsTested = 0;
        _healthyStreamCount = 0;
        _isResolving = false;
        _status = string.Empty;
        _progressSubject.OnNext(new StreamResolutionProgress());
    }

    public Task ReportCurrentStreamAsBadAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        var currentStream = streamSwitchingService.GetCurrentStream();
        if (currentStream == null)
        {
            return Task.CompletedTask;
        }

        return streamHealthReporter.ReportBadStreamAsync(
            currentStream.Stream.Url,
            currentStream.Stream.Url,
            StreamHealthIdentity.GetStreamName(currentStream.Stream),
            reason,
            cancellationToken);
    }

    private Task ReportHealthAsync(
        Game game,
        StreamModel stream,
        string status,
        string? error,
        StreamHealth? health = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stream.Url))
        {
            return Task.CompletedTask;
        }

        var report = new StreamHealthReport
        {
            StreamUrl = stream.Url,
            StreamName = StreamHealthIdentity.GetStreamName(stream),
            Status = status,
            Error = error,
            Bitrate = health?.Bitrate,
            Resolution = health?.Resolution,
            Framerate = health?.FrameRate,
            VideoCodec = health?.VideoCodec,
            AudioCodec = health?.AudioCodec,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId = sessionIdProvider.SessionId
        };

        return streamHealthService.ReportHealthAsync(game.ApiLeague, game.Home, game.Away, report, cancellationToken);
    }

    private async Task<PlaybackResult?> PlayStreamAsync(Game game, EnrichedStream enrichedStream,
        CancellationToken cancellationToken)
    {
        var gamePath = $"/{game.ApiLeague}/{game.Home}/{game.Away}";

        // Use the cached URL from health-check / prefetch if available (fast path).
        // If absent, resolve fresh now. Either way, if the first attempt fails we
        // resolve a brand-new URL and retry once — CDN tokens can be connection-bound
        // and the cached URL may have been invalidated since it was fetched.
        bool usedCachedUrl = !string.IsNullOrEmpty(enrichedStream.ResolvedM3U8Url);
        string? m3u8Url;
        if (usedCachedUrl)
        {
            logger.LogInformation("[StreamResolution] Using cached M3U8 for playback: {Channel}", enrichedStream.Stream.Channel);
            m3u8Url = enrichedStream.ResolvedM3U8Url;
        }
        else
        {
            logger.LogInformation("[StreamResolution] Resolving fresh M3U8 for playback: {Channel}", enrichedStream.Stream.Channel);
            m3u8Url = await apiService.ResolveM3U8ForPlaybackAsync(enrichedStream.Stream, gamePath, cancellationToken);
        }

        if (string.IsNullOrEmpty(m3u8Url))
        {
            logger.LogWarning("[StreamResolution] Failed to resolve m3u8 for {Channel}", enrichedStream.Stream.Channel);
            return PlaybackResult.Completed("Failed to resolve m3u8", true);
        }

        var title = $"{game.DisplayHome} vs {game.DisplayAway}";
        using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        selectionState.CurrentStream = enrichedStream.Stream;

        // Reset buffering flag and subscribe to buffering events
        _hasBufferingOccurred = false;
        EventHandler<bool>? bufferingHandler = (sender, isBuffering) =>
        {
            if (isBuffering)
            {
                _hasBufferingOccurred = true;
            }
        };
        nativeVideoPlayer.BufferingStateChanged += bufferingHandler;

        var reportingTask = StartPlaybackHealthReportingAsync(enrichedStream.Stream, playbackCts.Token);
        PlaybackResult? result;
        try
        {
            // Warm the next candidate while this one plays so the first Next click is instant.
            PrefetchUpcomingStreamUrl(game, cancellationToken);

            result = await nativeVideoPlayer.PlayVideoAsync(
                m3u8Url,
                string.IsNullOrWhiteSpace(enrichedStream.Referer) ? enrichedStream.Stream.Url : enrichedStream.Referer,
                title,
                () => HandleNextStreamRequestedAsync(game, cancellationToken),
                game.DisplayLeague,
                game.DisplayHome,
                game.DisplayAway,
                enrichedStream.RequestHeaders);
        }
        finally
        {
            nativeVideoPlayer.BufferingStateChanged -= bufferingHandler;
            playbackCts.Cancel();
            try
            {
                await reportingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // If we used a cached URL and it failed immediately, the token may have been
        // invalidated. Re-resolve fresh and retry this same stream once.
        if (usedCachedUrl && result != null && !result.Success &&
            result.Message?.Contains("User closed", StringComparison.OrdinalIgnoreCase) != true)
        {
            logger.LogInformation("[StreamResolution] Cached URL failed for {Channel} — retrying with fresh M3U8", enrichedStream.Stream.Channel);
            var freshUrl = await apiService.ResolveM3U8ForPlaybackAsync(enrichedStream.Stream, gamePath, cancellationToken);
            if (!string.IsNullOrEmpty(freshUrl) && !string.Equals(freshUrl, m3u8Url, StringComparison.OrdinalIgnoreCase))
            {
                enrichedStream.ResolvedM3U8Url = freshUrl;
                // Retry playback with the fresh URL — usedCachedUrl=false so no further retry loop.
                return await PlayStreamAsync(game, enrichedStream, cancellationToken);
            }
            logger.LogWarning("[StreamResolution] Fresh M3U8 unavailable or identical for {Channel} — treating as failed", enrichedStream.Stream.Channel);
        }

        return result;
    }

    private async Task StartPlaybackHealthReportingAsync(StreamModel stream, CancellationToken cancellationToken)
    {
        try
        {
            var hasReportedMetadata = false;
            var streamName = StreamHealthIdentity.GetStreamName(stream);

            // Wait for player to initialize and extract metadata
            // Windows MediaPlayer fires NaturalVideoSizeChanged after video decodes (~1-2s)
            await Task.Delay(TimeSpan.FromMilliseconds(2500), cancellationToken);

            // Get real metrics from the player service after playback has started
            var initialMetrics = nativeVideoPlayer.GetCurrentMetrics();
            _ = streamHealthReporter.ReportPlaybackStartedAsync(stream.Url, null, streamName, initialMetrics,
                cancellationToken);
            hasReportedMetadata = initialMetrics?.Resolution.HasValue == true;

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PlaybackHealthInterval, cancellationToken);

                var metrics = nativeVideoPlayer.GetCurrentMetrics();

                // Report periodic metrics without duplicating metadata
                _ = streamHealthReporter.ReportPlaybackMetricsAsync(stream.Url, null, streamName, metrics,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private PlaybackMetrics? BuildPlaybackMetrics()
    {
        try
        {
            var current = streamSwitchingService.GetCurrentStream();
            if (current == null)
            {
                return null;
            }

            var bitrate = current.Stream?.BitrateKbps ?? current.Health?.Bitrate;

            // Try to get current metrics from the native video player (includes video metadata)
            var playerMetrics = nativeVideoPlayer.GetCurrentMetrics();

            var metrics = new PlaybackMetrics
            {
                BitrateKbps = bitrate,
                Resolution = playerMetrics?.Resolution, // Get from player if available
                IsBuffering = _hasBufferingOccurred,
                VideoCodec = playerMetrics?.VideoCodec, // Get from player (extracted from format)
                AudioCodec = playerMetrics?.AudioCodec, // Get from player (extracted from format)
                Framerate = playerMetrics?.Framerate // Get from player (extracted from format)
            };

            // Clear buffering flag after reporting
            _hasBufferingOccurred = false;

            return metrics;
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleNextStreamRequestedAsync(Game game, CancellationToken cancellationToken)
    {
        var nextStream = streamSwitchingService.GetNextHealthyStream();
        if (nextStream == null)
        {
            logger.LogInformation("[StreamResolution] No next stream available");
            _status = "No next stream available";
            PublishProgress();
            return;
        }

        // Use the prefetched URL if available (instant). If not, resolve fresh now.
        // The caller (PlayStreamAsync) will retry with a fresh URL if the cached one 403s.
        if (string.IsNullOrEmpty(nextStream.ResolvedM3U8Url))
        {
            _status = "Switch requested - resolving stream URL...";
            PublishProgress();

            var resolved = await apiService.ResolveM3U8ForPlaybackAsync(
                nextStream.Stream,
                $"/{game.ApiLeague}/{game.Home}/{game.Away}",
                cancellationToken);

            if (string.IsNullOrEmpty(resolved))
            {
                logger.LogWarning("[StreamResolution] Failed to resolve m3u8 for next stream {Channel}",
                    nextStream.Stream.Channel);
                _status = "Switch failed - could not resolve stream URL";
                PublishProgress();
                return;
            }

            nextStream.ResolvedM3U8Url = resolved;
            logger.LogInformation("[StreamResolution] Resolved fresh M3U8 for next stream {Channel}",
                nextStream.Stream.Channel);
        }
        else
        {
            logger.LogInformation("[StreamResolution] Using prefetched M3U8 for next stream {Channel}",
                nextStream.Stream.Channel);
        }

        if (streamSwitchingService.SwitchToNextStream())
        {
            logger.LogInformation("[StreamResolution] Switched to next stream");
            _status = "Switched to next stream";
            PublishProgress();
            PrefetchUpcomingStreamUrl(game, cancellationToken);
        }
    }

    /// <summary>
    /// Warm the next candidate's m3u8 in the background so the following Next click stays
    /// instant without relying on a long-lived health-check URL.
    /// </summary>
    private void PrefetchUpcomingStreamUrl(Game game, CancellationToken cancellationToken)
    {
        var current = streamSwitchingService.GetCurrentStream();
        var upcoming = streamSwitchingService.GetNextHealthyStream();
        // With a single candidate, "next" wraps to current — don't re-resolve the live stream.
        if (upcoming?.Stream == null || ReferenceEquals(upcoming, current)) return;

        var channel = upcoming.Stream.Channel;
        var path = $"/{game.ApiLeague}/{game.Home}/{game.Away}";
        var stream = upcoming.Stream;

        _ = Task.Run(async () =>
        {
            try
            {
                var resolved = await apiService.ResolveM3U8ForPlaybackAsync(stream, path, cancellationToken);
                if (string.IsNullOrEmpty(resolved))
                {
                    logger.LogDebug(
                        "[StreamResolution] Prefetch skipped — no m3u8 for upcoming {Channel}",
                        channel);
                    return;
                }

                // Only write back if this is still the upcoming candidate (user may have switched).
                var stillUpcoming = streamSwitchingService.GetNextHealthyStream();
                if (stillUpcoming?.Stream == stream)
                {
                    stillUpcoming.ResolvedM3U8Url = resolved;
                    logger.LogInformation(
                        "[StreamResolution] Prefetched fresh M3U8 for upcoming stream {Channel}",
                        channel);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "[StreamResolution] Prefetch failed for upcoming {Channel}",
                    channel);
            }
        }, cancellationToken);
    }

    private async Task<bool> TryNextHealthyStreamAsync(Game game, CancellationToken cancellationToken)
    {
        var nextStream = streamSwitchingService.GetNextHealthyStream();
        if (nextStream == null)
        {
            _status = "All available streams failed - testing others...";
            PublishProgress();
            return false;
        }

        await PlayStreamAsync(game, nextStream, cancellationToken);
        return true;
    }

    private async Task HandlePlaybackFailureAsync(
        Game game,
        EnrichedStream enrichedStream,
        PlaybackResult playbackResult,
        CancellationToken cancellationToken)
    {
        _ = ReportHealthAsync(game, enrichedStream.Stream, "failed", playbackResult.Message, enrichedStream.Health,
            cancellationToken);

        streamSwitchingService.RemoveCurrentStream();
        _healthyStreamCount = streamSwitchingService.GetHealthyStreams().Count;
        PublishProgress();

        var playedNext = await TryNextHealthyStreamAsync(game, cancellationToken);
        if (!playedNext)
        {
            selectionCoordinator.ResumeTesting();
            _status = "Testing resumed";
            PublishProgress();
        }
    }

    private void PublishProgress()
    {
        _progressSubject.OnNext(new StreamResolutionProgress
        {
            IsResolving = _isResolving,
            Status = _status,
            TotalStreams = _totalStreams,
            StreamsTested = _streamsTested,
            HealthyStreams = _healthyStreamCount
        });
    }
}