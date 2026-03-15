using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using VardyParty.Models;
using VardyParty.Providers;
using VardyParty.Services;

namespace VardyParty.Orchestrators;

public class StreamResolutionOrchestrator(
    IApiService apiService,
    IStreamResolver streamResolver,
    IStreamSelectionCoordinator selectionCoordinator,
    IStreamHealthService streamHealthService,
    IStreamHealthReporter streamHealthReporter,
    ISessionIdProvider sessionIdProvider,
    IStreamSwitchingService streamSwitchingService,
    INativeVideoPlayerService nativeVideoPlayer,
    ILogger<StreamResolutionOrchestrator> logger) : IStreamResolutionOrchestrator
{
    private const int HealthyStreamsThreshold = 2;
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
                           3,
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
                _ = ReportHealthAsync(game, enrichedStream.Stream.Url, "failed", enrichedStream.ErrorMessage,
                    cancellationToken);
            }
            else if (enrichedStream.Status == StreamResolutionStatus.Healthy)
            {
                _ = ReportHealthAsync(game, enrichedStream.Stream.Url, "unknown", null, cancellationToken);
            }

            if (enrichedStream.Status != StreamResolutionStatus.Healthy)
            {
                continue;
            }

            streamSwitchingService.AddHealthyStream(enrichedStream);
            _healthyStreamCount = streamSwitchingService.GetHealthyStreams().Count;
            PublishProgress();

            if (_healthyStreamCount >= HealthyStreamsThreshold)
            {
                selectionCoordinator.PauseTesting();
                _status = "Testing paused";
                PublishProgress();
                break;
            }

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
                await HandlePlaybackFailureAsync(game, streamSwitchingService.GetCurrentStream()!, playbackResult,
                    cancellationToken);
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
            reason,
            cancellationToken);
    }

    private Task ReportHealthAsync(Game game, string? streamUrl, string status, string? error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return Task.CompletedTask;
        }

        var report = new StreamHealthReport
        {
            StreamUrl = streamUrl,
            Status = status,
            Error = error,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId = sessionIdProvider.SessionId
        };

        return streamHealthService.ReportHealthAsync(game.ApiLeague, game.Home, game.Away, report, cancellationToken);
    }

    private async Task<PlaybackResult?> PlayStreamAsync(Game game, EnrichedStream enrichedStream,
        CancellationToken cancellationToken)
    {
        // Use the M3U8 response cached from health check phase if available
        string? m3u8Url = null;

        if (!string.IsNullOrEmpty(enrichedStream.ResolvedM3U8Url))
        {
            logger.LogInformation("[StreamResolution] Reusing M3U8 from health check (token-based, used immediately)");
            m3u8Url = enrichedStream.ResolvedM3U8Url;
        }
        else
        {
            logger.LogInformation("[StreamResolution] M3U8 not cached, resolving fresh for playback");
            m3u8Url = await apiService.ResolveM3U8ForPlaybackAsync(
                enrichedStream.Stream,
                $"/{game.ApiLeague}/{game.Home}/{game.Away}",
                cancellationToken);
        }

        if (string.IsNullOrEmpty(m3u8Url))
        {
            logger.LogWarning("[StreamResolution] Failed to resolve m3u8 for {Channel}", enrichedStream.Stream.Channel);
            return PlaybackResult.Completed("Failed to resolve m3u8", true);
        }

        var title = $"{game.DisplayHome} vs {game.DisplayAway}";
        using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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

        var reportingTask = StartPlaybackHealthReportingAsync(enrichedStream.Stream.Url, playbackCts.Token);
        try
        {
            return await nativeVideoPlayer.PlayVideoAsync(
                m3u8Url,
                enrichedStream.Stream.Url,
                title,
                () => HandleNextStreamRequestedAsync(game, cancellationToken));
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
    }

    private async Task StartPlaybackHealthReportingAsync(string? streamUrl, CancellationToken cancellationToken)
    {
        try
        {
            var hasReportedMetadata = false;

            // Wait for player to initialize and extract metadata
            // Windows MediaPlayer fires NaturalVideoSizeChanged after video decodes (~1-2s)
            await Task.Delay(TimeSpan.FromMilliseconds(2500), cancellationToken);

            // Get real metrics from the player service after playback has started
            var initialMetrics = nativeVideoPlayer.GetCurrentMetrics();
            _ = streamHealthReporter.ReportPlaybackStartedAsync(streamUrl, null, initialMetrics, cancellationToken);
            hasReportedMetadata = initialMetrics?.Resolution.HasValue == true;

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PlaybackHealthInterval, cancellationToken);

                var metrics = nativeVideoPlayer.GetCurrentMetrics();

                // Report periodic metrics without duplicating metadata
                _ = streamHealthReporter.ReportPlaybackMetricsAsync(streamUrl, null, metrics, cancellationToken);
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

        if (streamSwitchingService.SwitchToNextStream())
        {
            logger.LogInformation("[StreamResolution] Switched to next stream");
            _status = "Switched to next stream";
            PublishProgress();
        }
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
        _ = ReportHealthAsync(game, enrichedStream.Stream.Url, "failed", playbackResult.Message, cancellationToken);

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