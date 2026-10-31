using VardyParty.Kernel;

namespace VardyParty.Playback;

/// <summary>
/// Cross-platform playback state machine. OS engines feed <see cref="MediaEngineEvent"/>;
/// hosts execute returned <see cref="PlaybackEffect"/>s (attach, pool, health, close).
/// </summary>
public sealed class PlaybackSessionController
{
    private StreamMetricsWindow _metricsWindow = new();
    private PlaybackSessionState _state = PlaybackSessionState.Idle;
    private long _attachGeneration;
    private string? _currentUrl;
    private string? _lastGoodUrl;
    private bool _hasEstablishedPlayback;
    private bool _isPreparing;
    private bool _usedCachedUrl;
    private bool _cacheRetryUsed;
    private int _healthyStreamCount;
    private bool _isBuffering;
    private int _consecutiveDownloadFailures;

    // Same-stream recovery: an established stream that errors is retried
    // (reload, then a fresh /play) before the session switches away from it.
    private readonly Func<DateTimeOffset> _clock;
    private bool _recovering;
    private bool _recoveryAttachPending;
    private int _sameStreamRetries;
    private DateTimeOffset? _lastSameStreamRetryAt;

    // Same-stream recovery of a SWITCH TARGET (a row we switched to that has not
    // played yet, with a last-good stream to fall back on). A failure that is not
    // confirmed dead retries the target first; if the budget runs out it reverts
    // to last good WITHOUT reporting or removing the target (docs "Failed switch").
    private bool _recoveringSwitch;

    // Audio-only detection (docs "Audio-only streams"). Per attach: Ready time, whether
    // the engine has shown a picture, and whether the session has confirmed it.
    private bool _videoConfirmed;
    private bool _videoSeen;
    private DateTimeOffset? _readyAt;

    // Feeds (m3u8 identity keys) that failed this cycle. A last-good stream whose feed
    // failed is never reverted to (docs "No loops").
    private readonly Dictionary<string, DateTimeOffset> _failedFeeds = new(StringComparer.OrdinalIgnoreCase);
    // Rows skipped as CDN unreachable (unreported, kept in the pool) since the
    // last Ready. A full lap of the pool with nothing playing closes the session
    // instead of cycling through blocked rows forever.
    private int _cdnUnreachableSkips;

    public PlaybackSessionController(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// When true, Ready alone does not make a stream "working": it must also show a
    /// picture (<see cref="MediaEngineEventKind.VideoProbe"/> with video). A stream that is
    /// Ready for <see cref="NoVideoTimeout"/> with no picture fails with
    /// <see cref="PlaybackPolicy.NoVideoReason"/>, and only a stream with video becomes
    /// last good. Android turns it on (ExoPlayer raises READY for audio-only HLS).
    /// </summary>
    /// <remarks>Set through <see cref="EnableVideoConfirmation"/> (a method, so property-filling
    /// test fixtures never turn it on by accident).</remarks>
    public bool RequireVideoConfirmation { get; private set; }

    /// <summary>How long after Ready a stream may go without a picture. Default <see cref="PlaybackPolicy.NoVideoTimeout"/>.</summary>
    public TimeSpan NoVideoTimeout { get; private set; } = PlaybackPolicy.NoVideoTimeout;

    /// <summary>Turns on video confirmation (Android). <paramref name="noVideoTimeout"/> overrides the default 8s.</summary>
    public void EnableVideoConfirmation(TimeSpan? noVideoTimeout = null)
    {
        RequireVideoConfirmation = true;
        if (noVideoTimeout is { } t && t > TimeSpan.Zero)
            NoVideoTimeout = t;
    }

    /// <summary>
    /// Called when a stream fails for a stream reason, BEFORE the effects run (the pool's
    /// current row is still the failing one): (url, reason, feedFailed). Infrastructure
    /// failures never call it. <see cref="PlaybackPoolCommandActions"/> wires it to
    /// <see cref="Ports.IStreamSwitchingService.MarkTriedInCycle"/>.
    /// </summary>
    public Action<string?, string, bool>? StreamFailedInCycle { get; private set; }

    /// <summary>Called when the current stream confirms video (clears its tried mark).</summary>
    public Action<string?>? StreamConfirmedInCycle { get; private set; }

    /// <summary>
    /// True when the pool has a stream other than the current one that has not failed
    /// this cycle. Next and auto-advance only go to untried streams; when there is none
    /// the session stops instead of looping. Null means "assume yes" (old behaviour).
    /// </summary>
    public Func<bool>? UntriedStreamAvailableProvider { get; private set; }

    /// <summary>
    /// Wires the cycle sinks (a method, not setters, so property-filling fixtures leave them null).
    /// <see cref="PlaybackPoolCommandActions"/> calls it with the pool.
    /// </summary>
    public void ConfigureCycleTracking(
        Action<string?, string, bool>? streamFailed,
        Action<string?>? streamConfirmed,
        Func<bool>? untriedStreamAvailable)
    {
        StreamFailedInCycle = streamFailed;
        StreamConfirmedInCycle = streamConfirmed;
        UntriedStreamAvailableProvider = untriedStreamAvailable;
    }

    /// <summary>True when <paramref name="url"/>'s feed failed this cycle.</summary>
    public bool IsFeedFailedInCycle(string? url)
    {
        var key = FeedKey(url);
        if (key.Length == 0 || !_failedFeeds.TryGetValue(key, out var at))
            return false;
        if (_clock() - at < PlaybackPolicy.FailedInCycleTtl)
            return true;
        _failedFeeds.Remove(key);
        return false;
    }

    /// <summary>
    /// Live pool size. When set, every engine event reads it first, so a stale
    /// count cannot close the session while the pool still has streams. Android
    /// used to sync only on user navigation: a count of 1 closed the player at
    /// 17:08:43 on 31 Oct 2026 with more streams in the pool.
    /// <see cref="PlaybackPoolCommandActions"/> wires it.
    /// </summary>
    public Func<int>? HealthyStreamCountProvider { get; set; }

    public PlaybackSessionSnapshot Snapshot => new()
    {
        State = _state,
        AttachGeneration = _attachGeneration,
        CurrentUrl = _currentUrl,
        LastGoodUrl = _lastGoodUrl,
        HasEstablishedPlayback = _hasEstablishedPlayback,
        IsPreparing = _isPreparing,
        UsedCachedUrl = _usedCachedUrl,
        CacheRetryUsed = _cacheRetryUsed,
        HealthyStreamCount = _healthyStreamCount,
        IsBuffering = _isBuffering,
        ConsecutiveDownloadFailures = _consecutiveDownloadFailures,
        IsRecovering = _recovering,
        IsRecoveringSwitch = _recoveringSwitch,
        SameStreamRetries = _sameStreamRetries,
        IsVideoConfirmed = _videoConfirmed
    };

    public StreamMetricsWindow MetricsWindow => _metricsWindow;

    /// <summary>Host updates pool size whenever healthy streams change.</summary>
    public void SetHealthyStreamCount(int count) => _healthyStreamCount = Math.Max(0, count);

    /// <summary>
    /// Begin attaching a URL. Increments generation. Call before the OS engine Prepare/SetSource.
    /// </summary>
    public IReadOnlyList<PlaybackEffect> BeginAttach(string url, bool usedCachedUrl = false, bool force = false)
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];

        if (!force && !PlaybackPolicy.CanAttach(_currentUrl, url, _isPreparing))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Attach rejected")];

        var isRecoveryAttach = _recoveryAttachPending;
        _recoveryAttachPending = false;

        _attachGeneration++;
        _currentUrl = url;
        _isPreparing = true;
        _isBuffering = false;
        _usedCachedUrl = usedCachedUrl;
        _consecutiveDownloadFailures = 0;
        _videoConfirmed = false;
        _videoSeen = false;
        _readyAt = null;
        // Cache retry flag is sticky for the stream attempt until established or failed start completes.

        if (isRecoveryAttach && _recovering)
        {
            // Fresh /play for the SAME stream: this is not a switch. A failure goes
            // back to the retry budget, never to revert-to-last-good (the same row).
            _state = PlaybackSessionState.Buffering;
            _metricsWindow = new StreamMetricsWindow();
        }
        else
        {
            // Any other attach is a different stream (start, user Next or Prev,
            // advance after a fail), so it gets a fresh retry budget.
            ResetSameStreamRecovery(clearBudget: true);
            if (_hasEstablishedPlayback)
                _state = PlaybackSessionState.Switching;
            else
                _state = PlaybackSessionState.Starting;
        }

        return
        [
            new PlaybackEffect(PlaybackEffectKind.Attach, Url: url, Generation: _attachGeneration)
        ];
    }

    public IReadOnlyList<PlaybackEffect> Handle(MediaEngineEvent engineEvent)
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];

        RefreshHealthyStreamCount();

        return engineEvent.Kind switch
        {
            MediaEngineEventKind.Ready => OnReady(engineEvent),
            MediaEngineEventKind.BufferingChanged => OnBuffering(engineEvent),
            MediaEngineEventKind.MetricsSample => OnMetrics(engineEvent),
            MediaEngineEventKind.Error => OnError(engineEvent),
            MediaEngineEventKind.Ended => OnEnded(engineEvent),
            MediaEngineEventKind.UserNext => OnUserNavigate(next: true),
            MediaEngineEventKind.UserPrevious => OnUserNavigate(next: false),
            MediaEngineEventKind.UserClose => OnUserClose(),
            MediaEngineEventKind.UserReportBad => OnUserReportBad(engineEvent.Message),
            MediaEngineEventKind.VideoProbe => OnVideoProbe(engineEvent),
            _ => [new PlaybackEffect(PlaybackEffectKind.None)]
        };
    }

    public IReadOnlyList<PlaybackEffect> NotifyFreshResolveUnavailable(string? reason = null)
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];

        RefreshHealthyStreamCount();
        var message = reason ?? "Fresh M3U8 unavailable";

        if (_recovering && _recoveringSwitch && _hasEstablishedPlayback)
        {
            // A switch target's fresh /play came back empty: spend an attempt; when
            // the budget is gone go back to last good, keeping the target unreported.
            _recoveryAttachPending = false;
            var retry = TryRecoverSameStream(message, PlaybackErrorKind.TokenOrPlaylistInvalid);
            return retry ?? RevertSwitchKeepingTarget(message);
        }

        if (_recovering && _hasEstablishedPlayback)
        {
            // A same-stream fresh /play came back empty. That can be a slow service,
            // so it uses up an attempt; the last attempt switches.
            _recoveryAttachPending = false;
            var retry = TryRecoverSameStream(message, PlaybackErrorKind.TokenOrPlaylistInvalid);
            return retry ?? FailEstablishedOrAdvance(message);
        }

        // Host tried RetryFreshResolve but got nothing / same URL — treat as failed start.
        _cacheRetryUsed = true;
        return FailStartOrAdvance(message);
    }

    /// <summary>
    /// A fresh <c>/play</c> (start retry or same-stream retry) could not get a
    /// verdict from the local service even after
    /// <see cref="InfrastructureRetryPolicy.FreshResolveDelays"/>: unreachable,
    /// timed out, its own 5xx, MP disabled or auth. That says nothing about the
    /// stream, so unlike <see cref="NotifyFreshResolveUnavailable"/> there is no
    /// ReportFailed, no RemoveCurrentFromPool and no same-stream budget spent.
    /// The session closes with <see cref="PlaybackCloseReasons.LocalServiceUnavailable"/>
    /// and the orchestrator defers: every row stays in the pool, unreported.
    /// It does not advance: the next row needs the same service, and a no-remove
    /// advance could ping-pong between rows.
    /// </summary>
    public IReadOnlyList<PlaybackEffect> NotifyFreshResolveInfrastructureUnavailable(string? reason = null)
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];

        if (_recoveringSwitch && _hasEstablishedPlayback && HasEligibleLastGood())
        {
            // Retrying a switch target and the service is unreachable: nothing was
            // learned about either stream. Keep watching last good, report nothing.
            var why = string.IsNullOrWhiteSpace(reason)
                ? PlaybackCloseReasons.LocalServiceUnavailable
                : $"{PlaybackCloseReasons.LocalServiceUnavailable}: {reason}";
            return RevertSwitchKeepingTarget(why, clearTargetUrl: false);
        }

        return CloseForInfrastructure(reason);
    }

    private List<PlaybackEffect> CloseForInfrastructure(string? reason)
    {
        ResetSameStreamRecovery(clearBudget: true);
        _isPreparing = false;
        _state = PlaybackSessionState.Failed;
        var message = string.IsNullOrWhiteSpace(reason)
            ? PlaybackCloseReasons.LocalServiceUnavailable
            : reason.StartsWith(PlaybackCloseReasons.LocalServiceUnavailable, StringComparison.OrdinalIgnoreCase)
                ? reason
                : $"{PlaybackCloseReasons.LocalServiceUnavailable}: {reason}";
        return
        [
            new PlaybackEffect(PlaybackEffectKind.Stop, Reason: message),
            new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: message)
        ];
    }

    /// <summary>
    /// The pool row about to be attached resolved (at <c>/play</c>) to a feed that already
    /// failed this cycle: sportylive, alxsports ch-9 and smlbn all resolved to premium261
    /// (audio only) on 31 Oct 2026. It is the same failure, so the row is skipped and
    /// dropped without being played or reported, and the session moves to the next untried
    /// stream (or stops when there is none).
    /// </summary>
    public IReadOnlyList<PlaybackEffect> NotifyDuplicateOfFailed(string reason)
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];

        RefreshHealthyStreamCount();
        ResetSameStreamRecovery(clearBudget: true);
        _isPreparing = false;
        // Row only: the feed is already marked.
        NotifyFailedInCycle(null, reason, feedFailed: false);

        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ClearResolvedUrl, Reason: reason),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: reason)
        };
        _healthyStreamCount = Math.Max(0, _healthyStreamCount - 1);
        if (_healthyStreamCount >= 1 && HasUntriedStream())
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: reason));
            return effects;
        }

        effects.AddRange(StopWithNothingUntried(reason));
        return effects;
    }

    /// <summary>
    /// The pool has nothing untried to attach. Go back to a last-good stream that showed
    /// video and has not failed, or stop with <see cref="PlaybackPolicy.NoWorkingStreamReason"/>.
    /// </summary>
    public IReadOnlyList<PlaybackEffect> NotifyNoUntriedStream()
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];
        ResetSameStreamRecovery(clearBudget: true);
        _isPreparing = false;
        return StopWithNothingUntried(PlaybackPolicy.NoWorkingStreamReason);
    }

    private List<PlaybackEffect> StopWithNothingUntried(string reason)
    {
        if (HasEligibleLastGood())
        {
            // Already on it (it is still playing): keep watching it.
            if (string.Equals(_lastGoodUrl, _currentUrl, StringComparison.OrdinalIgnoreCase))
                return [new PlaybackEffect(PlaybackEffectKind.None, Reason: PlaybackPolicy.NoOtherWorkingStreamText)];
            return [RevertToLastGoodEffect($"No untried stream - back to last good. ({reason})")];
        }

        _state = PlaybackSessionState.Failed;
        return [new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: PlaybackPolicy.NoWorkingStreamReason)];
    }

    /// <summary>
    /// Windows AdaptiveMediaSource download failures. After
    /// <see cref="PlaybackPolicy.MaxConsecutiveDownloadFailures"/> this is a hard Error.
    /// </summary>
    public IReadOnlyList<PlaybackEffect> NotifyDownloadFailure(string? reason = null)
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];

        RefreshHealthyStreamCount();
        _consecutiveDownloadFailures++;
        if (!PlaybackPolicy.IsHardDownloadFailure(_consecutiveDownloadFailures))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Download failure below threshold")];

        return OnError(new MediaEngineEvent
        {
            Kind = MediaEngineEventKind.Error,
            Generation = _attachGeneration,
            Message = reason ?? "Consecutive download failures"
        });
    }

    /// <summary>
    /// An index-driven switch (chip, Next, Prev) that started outside the engine
    /// event path replaces any same-stream retry in flight.
    /// </summary>
    public void CancelSameStreamRecovery() => ResetSameStreamRecovery(clearBudget: true);

    /// <summary>
    /// A successful segment/manifest download resets the consecutive-failure counter.
    /// </summary>
    public void NotifyDownloadSuccess() => _consecutiveDownloadFailures = 0;

    public void Reset()
    {
        _state = PlaybackSessionState.Idle;
        _attachGeneration = 0;
        _currentUrl = null;
        _lastGoodUrl = null;
        _hasEstablishedPlayback = false;
        _isPreparing = false;
        _usedCachedUrl = false;
        _cacheRetryUsed = false;
        _healthyStreamCount = 0;
        _isBuffering = false;
        _consecutiveDownloadFailures = 0;
        _metricsWindow = new StreamMetricsWindow();
        _videoConfirmed = false;
        _videoSeen = false;
        _readyAt = null;
        _failedFeeds.Clear();
        ResetSameStreamRecovery(clearBudget: true);
    }

    private IReadOnlyList<PlaybackEffect> OnReady(MediaEngineEvent e)
    {
        if (!PlaybackPolicy.IsCurrentGeneration(Snapshot, e.Generation))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Stale Ready")];

        _isPreparing = false;
        _isBuffering = false;
        _hasEstablishedPlayback = true;
        _cacheRetryUsed = false;
        _usedCachedUrl = false;
        _consecutiveDownloadFailures = 0;
        _cdnUnreachableSkips = 0;
        _state = PlaybackSessionState.Playing;
        // The retry worked. The budget refills once SameStreamRetryWindow passes
        // with no error.
        _recovering = false;
        _recoveryAttachPending = false;
        _recoveringSwitch = false;

        if (!RequireVideoConfirmation)
        {
            _lastGoodUrl = _currentUrl;
            return
            [
                new PlaybackEffect(PlaybackEffectKind.MarkEstablished, Url: _currentUrl, Generation: _attachGeneration),
                new PlaybackEffect(PlaybackEffectKind.ReportWorking, Url: _currentUrl, Generation: _attachGeneration)
            ];
        }

        // Ready is not "working" yet: ExoPlayer raises READY for an audio-only HLS feed
        // (premium261, 0x0, looping AAC). Last good and ReportWorking wait for a picture.
        _readyAt ??= _clock();
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.MarkEstablished, Url: _currentUrl, Generation: _attachGeneration)
        };
        if (!_videoConfirmed && _videoSeen)
            ConfirmVideo(effects);
        return effects;
    }

    private IReadOnlyList<PlaybackEffect> OnVideoProbe(MediaEngineEvent e)
    {
        if (!PlaybackPolicy.IsCurrentGeneration(Snapshot, e.Generation))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Stale VideoProbe")];

        if (!RequireVideoConfirmation || _videoConfirmed)
            return [new PlaybackEffect(PlaybackEffectKind.None)];

        if (e.HasVideo == true)
        {
            _videoSeen = true;
            if (_readyAt is null || _isPreparing)
                return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Video before Ready")];
            var effects = new List<PlaybackEffect>();
            ConfirmVideo(effects);
            return effects;
        }

        // Sound but no picture. Wait out a same-stream retry or a prepare first.
        if (_recovering || _isPreparing || _state is not (PlaybackSessionState.Playing or PlaybackSessionState.Buffering))
            return [new PlaybackEffect(PlaybackEffectKind.None)];

        if (!PlaybackPolicy.HasNoVideoTimedOut(_readyAt, _clock(), NoVideoTimeout))
            return [new PlaybackEffect(PlaybackEffectKind.None)];

        return FailNoVideo();
    }

    /// <summary>
    /// The stream played for <see cref="NoVideoTimeout"/> with no picture. It is a stream
    /// failure: reported failed (never working), its feed is marked failed for the cycle,
    /// and the session goes back to a last good stream with video, or on to the next
    /// untried stream. It is not retried: a reload gives the same audio-only feed.
    /// </summary>
    private List<PlaybackEffect> FailNoVideo()
    {
        var reason = PlaybackPolicy.NoVideoReason;
        _readyAt = null;
        ResetSameStreamRecovery(clearBudget: true);
        if (HasEligibleLastGood() && !SameFeed(_lastGoodUrl, _currentUrl))
            return FailSwitchAndRevert(reason);
        return FailEstablishedOrAdvance(reason);
    }

    private void ConfirmVideo(List<PlaybackEffect> effects)
    {
        _videoConfirmed = true;
        _lastGoodUrl = _currentUrl;
        var key = FeedKey(_currentUrl);
        if (key.Length > 0)
            _failedFeeds.Remove(key);
        try { StreamConfirmedInCycle?.Invoke(_currentUrl); }
        catch { /* the pool mark is advisory */ }
        effects.Add(new PlaybackEffect(PlaybackEffectKind.ReportWorking, Url: _currentUrl, Generation: _attachGeneration));
    }

    private IReadOnlyList<PlaybackEffect> OnBuffering(MediaEngineEvent e)
    {
        if (!PlaybackPolicy.IsCurrentGeneration(Snapshot, e.Generation))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Stale Buffering")];

        var buffering = e.IsBuffering == true;
        _isBuffering = buffering;

        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.RaiseBuffering, Reason: buffering ? "buffering" : "not-buffering", Generation: e.Generation)
        };

        if (buffering && _state is PlaybackSessionState.Playing or PlaybackSessionState.Buffering)
        {
            _state = PlaybackSessionState.Buffering;
            _metricsWindow.AddBufferingEvent();
            if (e.BitrateKbps is int br)
                _metricsWindow.AddBitrate(br);

            if (!_recovering && PlaybackPolicy.IsHealthDeclined(_metricsWindow))
                effects.AddRange(DeclineOrRecover("Health declined (buffering)"));
        }
        else if (!buffering && _state == PlaybackSessionState.Buffering && _hasEstablishedPlayback)
        {
            _state = PlaybackSessionState.Playing;
        }

        return effects;
    }

    private IReadOnlyList<PlaybackEffect> OnMetrics(MediaEngineEvent e)
    {
        if (!PlaybackPolicy.IsCurrentGeneration(Snapshot, e.Generation))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Stale Metrics")];

        _metricsWindow.ResetIfExpired();
        if (e.BitrateKbps is int br)
            _metricsWindow.AddBitrate(br);
        if (e.IsBuffering == true)
            _metricsWindow.AddBufferingEvent();

        if (_hasEstablishedPlayback &&
            !_recovering &&
            _state is PlaybackSessionState.Playing or PlaybackSessionState.Buffering &&
            PlaybackPolicy.IsHealthDeclined(_metricsWindow))
        {
            return DeclineOrRecover("Health declined (metrics)");
        }

        return [new PlaybackEffect(PlaybackEffectKind.None)];
    }

    private IReadOnlyList<PlaybackEffect> OnError(MediaEngineEvent e)
    {
        // Hosts must not raise Error for ExoPlayer null "error cleared" callbacks.
        if (!PlaybackPolicy.IsCurrentGeneration(Snapshot, e.Generation))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Stale Error")];

        _isPreparing = false;
        _metricsWindow.AddError();

        var reason = string.IsNullOrWhiteSpace(e.Message) ? "Playback error" : e.Message;
        var kind = PlaybackPolicy.ClassifyPlaybackError(reason);

        // A switch target that is already being retried errored again: keep
        // retrying it within the budget; dead reports and removes it; an empty
        // budget goes back to last good and keeps the target, unreported.
        if (_recoveringSwitch && _recovering && _hasEstablishedPlayback)
        {
            if (kind == PlaybackErrorKind.ConfirmedDead)
                return FailSwitchAndRevert(reason);
            return TryRecoverSameStream(reason, kind) ?? RevertSwitchKeepingTarget(reason);
        }

        // The stream that is playing (or a same-stream retry of it) errored:
        // retry it before switching away. Only a confirmed-dead error, or an
        // empty budget, switches.
        if (_hasEstablishedPlayback
            && (_recovering || _state is PlaybackSessionState.Playing or PlaybackSessionState.Buffering))
        {
            return TryRecoverSameStream(reason, kind) ?? FailEstablishedOrAdvance(reason);
        }

        if (PlaybackPolicy.ShouldRevertAfterFailedSwitch(Snapshot) && HasEligibleLastGood())
        {
            // Same classification as an established stream: only a confirmed-dead
            // switch target is reported and removed. A token/playlist blip ("Input
            // does not start with the #EXTM3U header") or a transient error retries
            // the target first (reload, then fresh /play). 4Kstream was dropped on
            // its first #EXTM3U blip at 20:26:20 UK on 31 Oct 2026 after playing 1080p.
            if (kind == PlaybackErrorKind.ConfirmedDead)
                return FailSwitchAndRevert(reason);

            var retry = TryRecoverSameStream(reason, kind);
            if (retry != null)
            {
                _recoveringSwitch = true;
                return retry;
            }

            return RevertSwitchKeepingTarget(reason);
        }

        // Established but still Switching should have hit ShouldRevert; if last-good missing, advance.
        if (_hasEstablishedPlayback)
            return FailEstablishedOrAdvance(reason);

        if (PlaybackPolicy.ShouldRetryFreshOnStartError(Snapshot, kind))
        {
            // Retry first, report later: ReportFailed is sent only if this retry
            // also fails (FailStartOrAdvance). A token rejected once is not a dead stream.
            _cacheRetryUsed = true;
            return
            [
                new PlaybackEffect(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason),
                new PlaybackEffect(PlaybackEffectKind.RetryFreshResolve, Url: _currentUrl, Reason: reason, Generation: _attachGeneration)
            ];
        }

        return FailStartOrAdvance(reason);
    }

    private IReadOnlyList<PlaybackEffect> OnEnded(MediaEngineEvent e)
    {
        if (!PlaybackPolicy.IsCurrentGeneration(Snapshot, e.Generation))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Stale Ended")];

        // End-of-stream: do not auto-advance by default (matches Android listener today).
        _isPreparing = false;
        return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Playback ended")];
    }

    private IReadOnlyList<PlaybackEffect> OnUserNavigate(bool next)
    {
        // Never mark bad on user navigation.
        if (!PlaybackPolicy.CanUserNavigate(Snapshot, _healthyStreamCount))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Navigate not allowed")];

        // Every other stream failed this cycle: stay put (Back still closes) instead of
        // cycling back onto a stream that already failed.
        if (!HasUntriedStream())
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: PlaybackPolicy.NoOtherWorkingStreamText)];

        // The user picked another stream, so drop any same-stream retry.
        ResetSameStreamRecovery(clearBudget: true);

        // Abandon a stuck/in-flight prepare so BeginAttach for the next URL is allowed.
        // Without this, CanAttach(isPreparing: true) rejects the index-driven switch
        // after AdvanceToNext (Android TV: toast only, player never changes).
        _isPreparing = false;

        return
        [
            new PlaybackEffect(
                next ? PlaybackEffectKind.AdvanceToNext : PlaybackEffectKind.AdvanceToPrevious,
                Reason: next ? "User next" : "User previous")
        ];
    }

    private IReadOnlyList<PlaybackEffect> OnUserClose()
    {
        _state = PlaybackSessionState.Closed;
        _isPreparing = false;
        return
        [
            new PlaybackEffect(PlaybackEffectKind.Stop),
            new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: PlaybackCloseReasons.UserClosed)
        ];
    }

    private IReadOnlyList<PlaybackEffect> OnUserReportBad(string? reason)
    {
        var message = reason ?? "User reported bad stream";
        NotifyFailedInCycle(_currentUrl, message, PlaybackPolicy.FailureMarksFeed(message));
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: message, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: message),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: message)
        };

        var remaining = Math.Max(0, _healthyStreamCount - 1);
        _healthyStreamCount = remaining;

        if (remaining >= 1 && HasUntriedStream())
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: message));
        }
        else if (remaining >= 1)
        {
            effects.AddRange(StopWithNothingUntried(message));
        }
        else
        {
            _state = PlaybackSessionState.Failed;
            effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: message));
        }

        return effects;
    }

    /// <summary>
    /// Plans the next same-stream retry, or returns null when the error is
    /// confirmed dead or the budget is empty (the caller then switches). Nothing
    /// is reported while retrying.
    /// </summary>
    private List<PlaybackEffect>? TryRecoverSameStream(
        string reason,
        PlaybackErrorKind kind,
        int maxAttempts = PlaybackPolicy.MaxSameStreamRetries)
    {
        var now = _clock();
        if (!_recovering && PlaybackPolicy.ShouldResetSameStreamRetries(_lastSameStreamRetryAt, now))
            _sameStreamRetries = 0;

        if (_sameStreamRetries >= maxAttempts || !PlaybackPolicy.ShouldRetrySameStream(kind, _sameStreamRetries))
            return null;

        _sameStreamRetries++;
        _lastSameStreamRetryAt = now;
        _recovering = true;
        // Every failure past the threshold re-enters OnError. Without this reset,
        // one burst of segment failures burned the whole budget in a single tick.
        _consecutiveDownloadFailures = 0;
        _isBuffering = true;
        _state = PlaybackSessionState.Buffering;
        _metricsWindow = new StreamMetricsWindow();

        var attempt = _sameStreamRetries;
        var label = $"Same-stream retry {attempt}/{PlaybackPolicy.MaxSameStreamRetries}";
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.RaiseBuffering, Reason: "buffering", Generation: _attachGeneration)
        };

        var step = PlaybackPolicy.NextSameStreamRetryStep(kind, attempt);
        if (step == SameStreamRetryStep.ReloadPlaylist && !string.IsNullOrWhiteSpace(_currentUrl))
        {
            // Re-attach the same URL in the same generation. A Ready or Error from
            // the reload counts for this stream, like a revert.
            _isPreparing = true;
            effects.Add(new PlaybackEffect(
                PlaybackEffectKind.Attach,
                Url: _currentUrl,
                Reason: $"{label}: reload playlist ({reason})",
                Generation: _attachGeneration));
            return effects;
        }

        _isPreparing = false;
        _recoveryAttachPending = true;
        effects.Add(new PlaybackEffect(
            PlaybackEffectKind.RetryFreshResolve,
            Url: _currentUrl,
            Reason: $"{label}: fresh /play ({reason})",
            Generation: _attachGeneration));
        return effects;
    }

    /// <summary>
    /// A health decline (too much buffering, low bitrate) gets one playlist
    /// reload, which clears a stale live window. A stream that is still
    /// degraded after that is declined and switched; it isn't worth three
    /// retries of a bad picture.
    /// </summary>
    private List<PlaybackEffect> DeclineOrRecover(string reason)
        => TryRecoverSameStream(reason, PlaybackErrorKind.Transient, PlaybackPolicy.MaxHealthDeclineRetries)
           ?? FailEstablishedOrAdvance(reason);

    private void ResetSameStreamRecovery(bool clearBudget)
    {
        _recovering = false;
        _recoveryAttachPending = false;
        _recoveringSwitch = false;
        if (clearBudget)
        {
            _sameStreamRetries = 0;
            _lastSameStreamRetryAt = null;
        }
    }

    private void RefreshHealthyStreamCount()
    {
        if (HealthyStreamCountProvider is not { } provider)
            return;

        try
        {
            _healthyStreamCount = Math.Max(0, provider());
        }
        catch
        {
            // Keep the last known count if the pool cannot be read.
        }
    }

    private List<PlaybackEffect> FailSwitchAndRevert(string reason)
    {
        ResetSameStreamRecovery(clearBudget: true);
        NotifyFailedInCycle(_currentUrl, reason, PlaybackPolicy.FailureMarksFeed(reason));
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: reason, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: reason)
        };

        _healthyStreamCount = Math.Max(0, _healthyStreamCount - 1);

        if (HasEligibleLastGood())
        {
            // Revert attaches last-good without treating it as a new switch failure loop.
            // Only a last good that showed video and has not failed this cycle.
            effects.Add(RevertToLastGoodEffect($"Switch failed — reverted to last good. ({reason})"));
        }
        else if (_healthyStreamCount >= 1 && HasUntriedStream())
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: reason));
        }
        else if (_healthyStreamCount >= 1)
        {
            effects.AddRange(StopWithNothingUntried(reason));
        }
        else
        {
            _state = PlaybackSessionState.Failed;
            effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: reason));
        }

        return effects;
    }

    /// <summary>
    /// A switch target failed without being proven dead (retries used up, or the
    /// service could not answer). Go back to the last-good stream and keep the
    /// target in the pool, unreported: a #EXTM3U blip or a slow /play is not a
    /// dead stream. Its resolved URL is cleared (usually a burned one-shot token)
    /// so the next visit gets a fresh /play.
    /// </summary>
    private List<PlaybackEffect> RevertSwitchKeepingTarget(string reason, bool clearTargetUrl = true)
    {
        ResetSameStreamRecovery(clearBudget: true);
        var effects = new List<PlaybackEffect>();
        if (clearTargetUrl)
        {
            // The target stays in the pool, unreported, but Next skips it for the cycle
            // (row only: a token blip says nothing about the feed). Infrastructure
            // (clearTargetUrl: false) marks nothing.
            NotifyFailedInCycle(_currentUrl, reason, feedFailed: false);
            effects.Add(new PlaybackEffect(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason));
        }

        if (HasEligibleLastGood())
        {
            effects.Add(RevertToLastGoodEffect($"Switch did not start - back to last good, stream kept. ({reason})"));
            return effects;
        }

        // Infrastructure never drops a stream: with nothing to go back to, defer.
        if (!clearTargetUrl)
            return CloseForInfrastructure(reason);

        // No last good to go back to: same as before (should not happen while switching).
        return FailEstablishedOrAdvance(reason);
    }

    /// <summary>
    /// Re-attach the last-good stream in the same generation. It showed video before, so
    /// it is confirmed again at once (no new no-video timer).
    /// </summary>
    private PlaybackEffect RevertToLastGoodEffect(string reason)
    {
        _isPreparing = false;
        _isBuffering = false;
        _state = PlaybackSessionState.Playing;
        _currentUrl = _lastGoodUrl;
        _metricsWindow = new StreamMetricsWindow();
        _videoConfirmed = true;
        _videoSeen = true;
        _readyAt = null;
        return new PlaybackEffect(
            PlaybackEffectKind.RevertToLastGood,
            Url: _lastGoodUrl,
            Reason: reason,
            Generation: _attachGeneration);
    }

    /// <summary>
    /// A last good stream that may be reverted to: it showed video (or Ready, when video
    /// confirmation is off) and its feed has not failed this cycle. premium261 was the
    /// "last good" every failed switch went back to on 31 Oct 2026.
    /// </summary>
    private bool HasEligibleLastGood()
        => !string.IsNullOrWhiteSpace(_lastGoodUrl) && !IsFeedFailedInCycle(_lastGoodUrl);

    private bool HasUntriedStream()
    {
        if (UntriedStreamAvailableProvider is not { } provider)
            return true;
        try
        {
            return provider();
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Records a stream failure for the cycle (pool row, and the feed when
    /// <paramref name="feedFailed"/>), and forgets last good when it is the stream that
    /// failed. Never called for infrastructure failures.
    /// </summary>
    private void NotifyFailedInCycle(string? url, string reason, bool feedFailed)
    {
        if (feedFailed)
        {
            var key = FeedKey(url);
            if (key.Length > 0)
                _failedFeeds[key] = _clock();
        }

        if (!string.IsNullOrWhiteSpace(url)
            && (string.Equals(_lastGoodUrl, url, StringComparison.OrdinalIgnoreCase)
                || (feedFailed && SameFeed(_lastGoodUrl, url))))
        {
            _lastGoodUrl = null;
        }

        try { StreamFailedInCycle?.Invoke(url, reason, feedFailed); }
        catch { /* the pool mark is advisory; the session decision stands */ }
    }

    private static string FeedKey(string? url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : StreamUrlNormalizer.M3u8IdentityKey(url);

    private static bool SameFeed(string? a, string? b)
    {
        var ka = FeedKey(a);
        return ka.Length > 0 && string.Equals(ka, FeedKey(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The CDN could not be reached (connect refused or timed out, reset, TLS) and
    /// the same-stream retries are used up. Not a dead stream: nothing is reported
    /// and the row stays in the pool (its resolved URL is cleared so the next visit
    /// gets a fresh <c>/play</c>). The session moves on to the next row. With no
    /// other row, or after a full lap with nothing playing, it closes with
    /// <see cref="PlaybackCloseReasons.CdnUnreachable"/>, which the orchestrator
    /// never reports.
    /// </summary>
    private List<PlaybackEffect> KeepCdnUnreachableAndMoveOn(string reason)
    {
        ResetSameStreamRecovery(clearBudget: true);
        _isPreparing = false;
        _cdnUnreachableSkips++;
        var message = $"{PlaybackCloseReasons.CdnUnreachable}: {reason}";
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: message)
        };

        // Not marked failed in the cycle (no verdict), but Next only goes to rows that
        // have not failed this cycle, so a pool of failed rows plus this one closes.
        if (_healthyStreamCount > 1 && _cdnUnreachableSkips < _healthyStreamCount && HasUntriedStream())
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: message));
            return effects;
        }

        _state = PlaybackSessionState.Failed;
        effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: message));
        return effects;
    }

    private List<PlaybackEffect> FailEstablishedOrAdvance(string reason)
    {
        // CDN unreachable is never dead: keep the row, unreported.
        if (PlaybackPolicy.ClassifyPlaybackError(reason) == PlaybackErrorKind.CdnUnreachable)
            return KeepCdnUnreachableAndMoveOn(reason);

        // Retries are used up or the stream is confirmed dead: report it once and
        // switch. The next stream starts with a fresh budget.
        ResetSameStreamRecovery(clearBudget: true);
        NotifyFailedInCycle(_currentUrl, reason, PlaybackPolicy.FailureMarksFeed(reason));
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: reason, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: reason)
        };

        _healthyStreamCount = Math.Max(0, _healthyStreamCount - 1);
        _isPreparing = false;

        if (_healthyStreamCount >= 1 && HasUntriedStream())
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: reason));
            // Host will BeginAttach the next URL → Switching/Starting.
        }
        else if (_healthyStreamCount >= 1)
        {
            // Every other stream failed this cycle: stop rather than loop.
            effects.AddRange(StopWithNothingUntried(reason));
        }
        else
        {
            _state = PlaybackSessionState.Failed;
            effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: reason));
        }

        // Decline reports use ReportDeclined when reason indicates soft decline.
        if (reason.Contains("declined", StringComparison.OrdinalIgnoreCase))
        {
            effects[0] = new PlaybackEffect(PlaybackEffectKind.ReportDeclined, Reason: reason, Generation: _attachGeneration);
        }

        return effects;
    }

    private List<PlaybackEffect> FailStartOrAdvance(string reason)
    {
        // CDN unreachable is never dead: keep the row, unreported.
        if (PlaybackPolicy.ClassifyPlaybackError(reason) == PlaybackErrorKind.CdnUnreachable)
            return KeepCdnUnreachableAndMoveOn(reason);

        ResetSameStreamRecovery(clearBudget: true);
        NotifyFailedInCycle(_currentUrl, reason, PlaybackPolicy.FailureMarksFeed(reason));
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: reason, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: reason)
        };

        _healthyStreamCount = Math.Max(0, _healthyStreamCount - 1);
        _isPreparing = false;
        _hasEstablishedPlayback = false;

        if (PlaybackPolicy.ShouldAdvanceAfterFailedStart(Snapshot, _healthyStreamCount) && HasUntriedStream())
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: reason));
        }
        else if (PlaybackPolicy.ShouldAdvanceAfterFailedStart(Snapshot, _healthyStreamCount))
        {
            _state = PlaybackSessionState.Failed;
            effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: PlaybackPolicy.NoWorkingStreamReason));
        }
        else
        {
            _state = PlaybackSessionState.Failed;
            effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: reason));
        }

        return effects;
    }
}
