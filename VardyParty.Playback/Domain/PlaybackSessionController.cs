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

    public PlaybackSessionController(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
        SameStreamRetries = _sameStreamRetries
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
            _ => [new PlaybackEffect(PlaybackEffectKind.None)]
        };
    }

    public IReadOnlyList<PlaybackEffect> NotifyFreshResolveUnavailable(string? reason = null)
    {
        if (_state == PlaybackSessionState.Closed)
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Session closed")];

        RefreshHealthyStreamCount();
        var message = reason ?? "Fresh M3U8 unavailable";

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
        ResetSameStreamRecovery(clearBudget: true);
    }

    private IReadOnlyList<PlaybackEffect> OnReady(MediaEngineEvent e)
    {
        if (!PlaybackPolicy.IsCurrentGeneration(Snapshot, e.Generation))
            return [new PlaybackEffect(PlaybackEffectKind.None, Reason: "Stale Ready")];

        _isPreparing = false;
        _isBuffering = false;
        _hasEstablishedPlayback = true;
        _lastGoodUrl = _currentUrl;
        _cacheRetryUsed = false;
        _usedCachedUrl = false;
        _consecutiveDownloadFailures = 0;
        _state = PlaybackSessionState.Playing;
        // The retry worked. The budget refills once SameStreamRetryWindow passes
        // with no error.
        _recovering = false;
        _recoveryAttachPending = false;

        return
        [
            new PlaybackEffect(PlaybackEffectKind.MarkEstablished, Url: _currentUrl, Generation: _attachGeneration),
            new PlaybackEffect(PlaybackEffectKind.ReportWorking, Url: _currentUrl, Generation: _attachGeneration)
        ];
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

        // The stream that is playing (or a same-stream retry of it) errored:
        // retry it before switching away. Only a confirmed-dead error, or an
        // empty budget, switches.
        if (_hasEstablishedPlayback
            && (_recovering || _state is PlaybackSessionState.Playing or PlaybackSessionState.Buffering))
        {
            return TryRecoverSameStream(reason, kind) ?? FailEstablishedOrAdvance(reason);
        }

        if (PlaybackPolicy.ShouldRevertAfterFailedSwitch(Snapshot))
            return FailSwitchAndRevert(reason);

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
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: message, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: message),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: message)
        };

        var remaining = Math.Max(0, _healthyStreamCount - 1);
        _healthyStreamCount = remaining;

        if (remaining >= 1)
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: message));
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
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: reason, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: reason)
        };

        _healthyStreamCount = Math.Max(0, _healthyStreamCount - 1);

        if (!string.IsNullOrWhiteSpace(_lastGoodUrl))
        {
            // Revert attaches last-good without treating it as a new switch failure loop.
            _isPreparing = false;
            _state = PlaybackSessionState.Playing;
            _currentUrl = _lastGoodUrl;
            effects.Add(new PlaybackEffect(
                PlaybackEffectKind.RevertToLastGood,
                Url: _lastGoodUrl,
                Reason: $"Switch failed — reverted to last good. ({reason})",
                Generation: _attachGeneration));
        }
        else if (_healthyStreamCount >= 1)
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: reason));
        }
        else
        {
            _state = PlaybackSessionState.Failed;
            effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: reason));
        }

        return effects;
    }

    private List<PlaybackEffect> FailEstablishedOrAdvance(string reason)
    {
        // Retries are used up or the stream is confirmed dead: report it once and
        // switch. The next stream starts with a fresh budget.
        ResetSameStreamRecovery(clearBudget: true);
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: reason, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: reason)
        };

        _healthyStreamCount = Math.Max(0, _healthyStreamCount - 1);
        _isPreparing = false;

        if (_healthyStreamCount >= 1)
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: reason));
            // Host will BeginAttach the next URL → Switching/Starting.
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
        ResetSameStreamRecovery(clearBudget: true);
        var effects = new List<PlaybackEffect>
        {
            new(PlaybackEffectKind.ReportFailed, Reason: reason, Generation: _attachGeneration),
            new(PlaybackEffectKind.ClearResolvedUrl, Url: _currentUrl, Reason: reason),
            new(PlaybackEffectKind.RemoveCurrentFromPool, Reason: reason)
        };

        _healthyStreamCount = Math.Max(0, _healthyStreamCount - 1);
        _isPreparing = false;
        _hasEstablishedPlayback = false;

        if (PlaybackPolicy.ShouldAdvanceAfterFailedStart(Snapshot, _healthyStreamCount))
        {
            effects.Add(new PlaybackEffect(PlaybackEffectKind.AdvanceToNext, Reason: reason));
        }
        else
        {
            _state = PlaybackSessionState.Failed;
            effects.Add(new PlaybackEffect(PlaybackEffectKind.CloseSession, Reason: reason));
        }

        return effects;
    }
}
