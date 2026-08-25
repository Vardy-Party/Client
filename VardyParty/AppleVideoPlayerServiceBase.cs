#if IOS || MACCATALYST
using AVFoundation;
using AVKit;
using CoreMedia;
using Foundation;
using Microsoft.Extensions.Logging;
using UIKit;
using VardyParty.Models;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Platforms.Apple;

/// <summary>
/// Apple AVPlayer host. Session, command execution, and pool actions come from
/// <c>VardyParty.Playback</c> — not <c>VardyParty.Linux</c>. Subclasses only create the asset.
/// </summary>
public abstract class AppleVideoPlayerServiceBase : INativeVideoPlayerService
{
    private readonly ILogger _logger;
    private readonly IStreamSwitchingService _switching;
    private readonly IApiService _api;
    private readonly IStreamHealthReporter? _healthReporter;
    private readonly PlaybackSessionController _session = new();
    private readonly DelegatingMediaEngine _engine = new();
    private readonly PlaybackPoolCommandActions _pool;
    private readonly List<NSObject> _notificationObservers = [];

    private TaskCompletionSource<PlaybackResult>? _playbackTcs;
    private Func<Task>? _onNextStreamRequested;
    private string? _refererUrl;
    private IReadOnlyDictionary<string, string>? _requestHeaders;
    private string? _title;
    private bool _closing;
    private AVPlayer? _player;
    private AVPlayerItem? _item;
    private SessionPlayerViewController? _playerVc;
    private IDisposable? _statusObserver;
    private PlaybackMetrics? _currentMetrics;

    public event EventHandler<bool>? BufferingStateChanged;

    protected AppleVideoPlayerServiceBase(
        ILogger logger,
        IStreamSwitchingService switching,
        IApiService api,
        IStreamHealthReporter? healthReporter)
    {
        _logger = logger;
        _switching = switching;
        _api = api;
        _healthReporter = healthReporter;
        _pool = new PlaybackPoolCommandActions(
            _session,
            _switching,
            ResolveFreshM3U8Async,
            AttachViaSession,
            ApplyPlaybackCommand);
        _engine.EngineEvent += (_, engineEvent) => DispatchEngine(engineEvent);
        _engine.MetricsHandler = GetCurrentMetrics;
        _engine.AttachHandler = AttachAvPlayerAsync;
        _engine.StopHandler = _ =>
        {
            MainThread.BeginInvokeOnMainThread(() => _player?.Pause());
            return Task.CompletedTask;
        };
    }

    public PlaybackMetrics? GetCurrentMetrics() => _currentMetrics;

    public async Task<PlaybackResult> PlayVideoAsync(
        string m3u8Url,
        string refererUrl,
        string title,
        Func<Task>? onNextStreamRequested = null,
        string? league = null,
        string? homeTeam = null,
        string? awayTeam = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        _logger.LogInformation("[AppleVideoPlayer] Playing video: {Title}", title);
        _onNextStreamRequested = onNextStreamRequested;
        _playbackTcs = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refererUrl = refererUrl;
        _requestHeaders = requestHeaders;
        _title = title;
        _closing = false;

        try
        {
            _session.Reset();
            AttachViaSession(m3u8Url);
            return await _playbackTcs.Task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AppleVideoPlayer] Error during playback");
            return PlaybackResult.Completed($"Playback error: {ex.Message}", true);
        }
    }

    protected abstract AVUrlAsset CreateAsset(
        string m3u8Url,
        string referer,
        IReadOnlyDictionary<string, string>? requestHeaders);

    private void DispatchEngine(MediaEngineEvent engineEvent)
    {
        try
        {
            var cmd = PlaybackCommand.FromEffects(_session.Handle(engineEvent));
            ApplyPlaybackCommand(cmd);
            if (engineEvent.Kind == MediaEngineEventKind.Ended && !cmd.CloseSession)
                CompletePlayback(PlaybackResult.SuccessResult("Stream ended."), dismiss: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AppleVideoPlayer] DispatchEngine failed ({Kind})", engineEvent.Kind);
        }
    }

    private void AttachViaSession(string url, bool usedCachedUrl = false, bool force = false)
    {
        _session.SetHealthyStreamCount(_switching.GetHealthyStreams().Count);
        ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.BeginAttach(url, usedCachedUrl, force)));
    }

    private void ApplyPlaybackCommand(PlaybackCommand cmd)
        => PlaybackCommandExecutor.Apply(cmd, new ApplePlaybackCommandHost(this));

    private Task AttachAvPlayerAsync(
        string mediaUrl,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                AttachAvPlayerOnMain(mediaUrl, requestHeaders ?? _requestHeaders);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AppleVideoPlayer] Attach failed");
                tcs.TrySetException(ex);
                _engine.Raise(MediaEngineEvent.Error(_session.Snapshot.AttachGeneration, ex.Message));
            }
        });
        return tcs.Task;
    }

    private void AttachAvPlayerOnMain(string m3u8Url, IReadOnlyDictionary<string, string>? requestHeaders)
    {
        UnhookItem();
        var asset = CreateAsset(m3u8Url, _refererUrl ?? string.Empty, requestHeaders);
        var item = new AVPlayerItem(asset);
        _item = item;
        HookItem(item);

        if (_player == null)
        {
            _player = new AVPlayer(item);
            PresentPlayer(_player);
        }
        else
        {
            _player.ReplaceCurrentItemWithPlayerItem(item);
            _player.Play();
        }
    }

    private void HookItem(AVPlayerItem item)
    {
        _statusObserver = item.AddObserver(
            new NSString("status"),
            NSKeyValueObservingOptions.New,
            _ =>
            {
                if (item.Status == AVPlayerItemStatus.ReadyToPlay)
                {
                    ExtractVideoMetadata(item);
                    _engine.Raise(MediaEngineEvent.Ready(_session.Snapshot.AttachGeneration));
                }
                else if (item.Status == AVPlayerItemStatus.Failed)
                {
                    var message = item.Error?.LocalizedDescription ?? "Playback failed";
                    _engine.Raise(MediaEngineEvent.Error(_session.Snapshot.AttachGeneration, message));
                }
            });

        Observe(AVPlayerItem.PlaybackStalledNotification, item, () =>
        {
            BufferingStateChanged?.Invoke(this, true);
            _engine.Raise(MediaEngineEvent.Buffering(_session.Snapshot.AttachGeneration, true));
        });
        Observe(AVPlayerItem.TimeJumpedNotification, item, () =>
        {
            BufferingStateChanged?.Invoke(this, false);
            _engine.Raise(MediaEngineEvent.Buffering(_session.Snapshot.AttachGeneration, false));
        });
        Observe(AVPlayerItem.DidPlayToEndTimeNotification, item, () =>
            _engine.Raise(MediaEngineEvent.Ended(_session.Snapshot.AttachGeneration)));
        Observe(AVPlayerItem.ItemFailedToPlayToEndTimeNotification, item, () =>
        {
            var message = item.Error?.LocalizedDescription ?? "Playback failed";
            _engine.Raise(MediaEngineEvent.Error(_session.Snapshot.AttachGeneration, message));
        });
    }

    private void Observe(NSString name, NSObject from, Action handler)
    {
        _notificationObservers.Add(NSNotificationCenter.DefaultCenter.AddObserver(name, _ => handler(), from));
    }

    private void UnhookItem()
    {
        _statusObserver?.Dispose();
        _statusObserver = null;
        foreach (var observer in _notificationObservers)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
            observer.Dispose();
        }

        _notificationObservers.Clear();
        _item = null;
    }

    private void PresentPlayer(AVPlayer player)
    {
        var vc = GetTopViewController();
        if (vc == null)
            throw new InvalidOperationException("No active view controller available.");

        _playerVc = new SessionPlayerViewController
        {
            Player = player,
            ShowsPlaybackControls = true,
            ModalPresentationStyle = UIModalPresentationStyle.FullScreen,
            Title = _title,
            Dismissed = OnPlayerDismissed
        };
        vc.PresentViewController(_playerVc, true, () => player.Play());
    }

    private void OnPlayerDismissed()
    {
        if (_closing)
            return;
        _engine.Raise(MediaEngineEvent.UserClose());
    }

    private void DismissPlayerUi()
    {
        try
        {
            _playerVc?.DismissViewController(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AppleVideoPlayer] Dismiss failed");
        }

        _playerVc = null;
        UnhookItem();
        _player?.Pause();
        _player = null;
    }

    private void CompletePlayback(PlaybackResult result, bool dismiss)
    {
        if (dismiss)
            DismissPlayerUi();
        _playbackTcs?.TrySetResult(result);
    }

    private Task<string?> ResolveFreshM3U8Async(EnrichedStream current, CancellationToken cancellationToken)
    {
        if (current.Stream == null)
            return Task.FromResult<string?>(null);

        return _api.ResolveM3U8ForPlaybackAsync(current.Stream, current.Referer ?? string.Empty, cancellationToken);
    }

    private static UIViewController? GetTopViewController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(s => s.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);

        var root = window?.RootViewController;
        if (root == null)
            return null;

        while (root.PresentedViewController is { } presented)
            root = presented;

        return root;
    }

    private void ExtractVideoMetadata(AVPlayerItem? playerItem)
    {
        if (playerItem?.Asset == null)
            return;

        try
        {
            var metrics = new PlaybackMetrics();
            var videoTracks = playerItem.Asset.TracksWithMediaType(AVMediaTypes.Video.GetConstant()!);
            if (videoTracks is { Length: > 0 })
            {
                var videoTrack = videoTracks[0];
                var dimensions = videoTrack.NaturalSize;
                if (dimensions.Width > 0 && dimensions.Height > 0)
                    metrics.Resolution = ((int)dimensions.Width, (int)dimensions.Height);

                var framerate = (int)videoTrack.NominalFrameRate;
                if (framerate > 0)
                    metrics.Framerate = framerate;

                var formatDescriptions = videoTrack.FormatDescriptions;
                if (formatDescriptions is { Length: > 0 } && formatDescriptions[0] is CMFormatDescription formatDesc)
                    metrics.VideoCodec = CodecFourccToFriendlyName(formatDesc.MediaSubType);
            }

            var audioTracks = playerItem.Asset.TracksWithMediaType(AVMediaTypes.Audio.GetConstant()!);
            if (audioTracks is { Length: > 0 })
            {
                var formatDescriptions = audioTracks[0].FormatDescriptions;
                if (formatDescriptions is { Length: > 0 } && formatDescriptions[0] is CMFormatDescription formatDesc)
                    metrics.AudioCodec = CodecFourccToFriendlyName(formatDesc.MediaSubType);
            }

            _currentMetrics = metrics;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AppleVideoPlayer] Failed to extract video metadata");
        }
    }

    private static string? CodecFourccToFriendlyName(uint fourcc)
        => fourcc switch
        {
            0x61637631 => "H.264",
            0x68657631 => "H.265",
            0x76703039 => "VP9",
            0x56503830 => "VP8",
            0x61763031 => "AV1",
            0x6d703461 => "AAC",
            0x616c6163 => "AAC-LC",
            0x61632d33 => "AC-3",
            0x65632d33 => "E-AC-3",
            0x6f707573 => "Opus",
            0x666c6163 => "FLAC",
            0x2e6d7033 => "MP3",
            _ => null
        };

    private sealed class SessionPlayerViewController : AVPlayerViewController
    {
        public Action? Dismissed { get; set; }

        public override void ViewDidDisappear(bool animated)
        {
            base.ViewDidDisappear(animated);
            if (IsBeingDismissed)
                Dismissed?.Invoke();
        }
    }

    private sealed class ApplePlaybackCommandHost(AppleVideoPlayerServiceBase player) : IPlaybackCommandHost
    {
        public void BeginIndexSwitchSuppression()
        {
        }

        public void EndIndexSwitchSuppression()
        {
        }

        public void ClearCurrentResolvedUrl() => player._pool.ClearCurrentResolvedUrl();

        public void RemoveCurrentFromPool() => player._pool.RemoveCurrentFromPool();

        public void SyncHealthyStreamCount() => player._pool.SyncHealthyStreamCount();

        public void ReportFailed(string? reason)
        {
            player._logger.LogWarning("[AppleVideoPlayer] Stream failed: {Reason}", reason);
            var url = player._session.Snapshot.CurrentUrl;
            if (player._healthReporter != null)
                _ = player._healthReporter.ReportPlaybackErrorAsync(url, player._refererUrl, error: reason);
        }

        public void ReportDeclined(string? reason)
        {
            player._logger.LogWarning("[AppleVideoPlayer] Stream declined: {Reason}", reason);
            var url = player._session.Snapshot.CurrentUrl;
            if (player._healthReporter != null)
                _ = player._healthReporter.ReportPlaybackErrorAsync(url, player._refererUrl, error: reason);
        }

        public void RaiseBuffering(bool isBuffering)
        {
            player.BufferingStateChanged?.Invoke(player, isBuffering);
            if (isBuffering && player._healthReporter != null)
            {
                _ = player._healthReporter.ReportBufferingAsync(
                    player._session.Snapshot.CurrentUrl,
                    player._refererUrl,
                    metrics: player._currentMetrics);
            }
        }

        public void Attach(string url, bool isRevert)
        {
            if (isRevert)
                player._logger.LogWarning("[AppleVideoPlayer] Reverting to last good stream: {Url}", url);
            _ = player._engine.AttachAsync(url, player._requestHeaders);
        }

        public void AttachCurrentAfterRemove() => _ = player._pool.AttachCurrentFromPoolAsync();

        public void RetryFreshResolve() => _ = player._pool.RetryFreshResolveAsync();

        public void StopEngine()
            => MainThread.BeginInvokeOnMainThread(() => player._player?.Pause());

        public void CloseSession(string reason)
        {
            player._closing = true;
            player.CompletePlayback(PlaybackResult.Completed(reason, true), dismiss: true);
        }

        public void SwitchPoolToNext()
        {
            if (player._onNextStreamRequested != null)
                _ = player._onNextStreamRequested();
        }

        public void SwitchPoolToPrevious() => player._pool.SwitchPoolToPrevious();

        public void NotifyApplyFailed(Exception exception)
            => player._logger.LogWarning(exception, "[AppleVideoPlayer] ApplyPlaybackCommand failed");
    }
}
#endif
