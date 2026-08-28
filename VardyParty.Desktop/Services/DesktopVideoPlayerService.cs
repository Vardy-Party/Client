using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using VardyParty.Hosting;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Desktop.Services;

/// <summary>
/// LibVLC playback for the Linux/desktop head, ported from the retired
/// VardyParty.Linux head's LinuxVideoPlayerService.
///
/// Rendering surface: with the EmbeddedDesktopVideo build switch ON (the
/// default), playback renders INSIDE the app window — DesktopHomePage hosts a
/// VideoHostView (LibVLCSharp.Avalonia's VideoView bridged through the
/// MAUI-Avalonia AvaloniaControlHandler) and hands this service an
/// <see cref="EmbedSurfaceAsync"/> delegate that attaches the MediaPlayer to
/// that hosted native child window before Play. If the hosted surface cannot
/// be attached at runtime (handler gap, compositor refusal, drawable never
/// realized) the service logs once and falls back — for that playback session
/// — to the pre-feature behaviour: no drawable attached, libvlc opens its own
/// native video window (the same "playback happens on a dedicated native
/// surface" model the Android head uses with NativeVideoActivity). With the
/// switch OFF the standalone-window path is the only one compiled in.
///
/// UI-thread invariant (field failure: under WSL a wedged libvlc froze the
/// whole app — the Close button was unclickable): NO libvlc call ever runs on
/// the caller's thread. Init, attach/play, stop and dispose all run as worker
/// ops with timeouts (<see cref="RunVlcOpAsync"/>); an op that does not
/// complete in time marks the whole LibVLC+MediaPlayer pair ABANDONED — never
/// awaited, never touched again (the hung thread leaks with it) — and the
/// next play builds a fresh pair. Close (<see cref="StopPlayback"/>)
/// completes the session immediately and tears libvlc down fire-and-forget,
/// so the Close control stays responsive even mid-wedge.
///
/// WSL hardening: under WSL (/proc/version contains "microsoft") — or with
/// VARDYPARTY_DESKTOP_VLC_SAFE=1 — libvlc gets conservative options:
/// software decode, plain X11 vout, Pulse aout (WSLg), no hardware probing.
/// Safe under xvfb too. Audio is never disabled (<c>--no-audio</c> is not
/// an option); see <see cref="DesktopPlatformProbe.BuildLibVlcOptions"/>.
/// HLS is fetched through a local DualStack+Referer bridge
/// (<see cref="LibVlcRefererProxy"/>): LibVLC's native HTTP demuxer has
/// cancelled on WSL for CDNs that HttpClient health-checks successfully.
///
/// LibVLC initialisation is lazy (first PlayVideoAsync), never in the startup
/// path: machines without libvlc installed (or headless CI) get a logged
/// playback error instead of a startup crash.
/// </summary>
public class DesktopVideoPlayerService : INativeVideoPlayerService, IDisposable
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<DesktopVideoPlayerService> _logger;
    private readonly IStreamSwitchingService _switching;
    private readonly IStreamHealthReporter _healthReporter;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PlaybackSessionController _session = new();
    private readonly DelegatingMediaEngine _engine = new();
    private readonly PlaybackPoolCommandActions _pool;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private VlcSession? _current;
    private int _nextVlcGeneration;
    private Media? _currentMedia;
    private LibVlcRefererProxy? _refererProxy;
    private long _demuxFailureGeneration = -1;
    private TaskCompletionSource<PlaybackResult>? _playbackTcs;
    private Func<Task>? _onNextStreamRequested;
    private bool _isBuffering;
    private bool _initFailed;
    private string? _refererUrl;
    private IReadOnlyDictionary<string, string>? _requestHeaders;
    private Timer? _metricsTimer;
    private int _playbackSessionId;

    public event EventHandler<bool>? BufferingStateChanged;
    public event EventHandler<bool>? PlaybackVisibilityChanged;

    /// <summary>
    /// One LibVLC+MediaPlayer generation. Ops serialize on <see cref="OpGate"/>;
    /// a timed-out op flips <see cref="Abandoned"/> and the pair is never
    /// touched again (disposal included — a wedged libvlc that won't die is
    /// leaked deliberately, not awaited).
    /// </summary>
    private sealed class VlcSession
    {
        public required int Generation { get; init; }
        public required LibVLC LibVlc { get; init; }
        public required MediaPlayer Player { get; init; }
        public SemaphoreSlim OpGate { get; } = new(1, 1);
        public volatile bool Abandoned;
        public EventHandler<LogEventArgs>? LogHandler;
    }

    public DesktopVideoPlayerService(
        ILogger<DesktopVideoPlayerService> logger,
        IStreamSwitchingService switching,
        ResolveFreshPlaybackUrlAsync resolveFresh,
        IStreamHealthReporter healthReporter,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _switching = switching;
        _healthReporter = healthReporter;
        _httpClientFactory = httpClientFactory;
        _pool = new PlaybackPoolCommandActions(
            _session,
            _switching,
            resolveFresh,
            AttachViaSession,
            ApplyPlaybackCommand);
        _engine.EngineEvent += (_, engineEvent) => DispatchEngine(engineEvent);
        _engine.MetricsHandler = GetCurrentMetrics;
        _engine.AttachHandler = AttachLibVlcAsync;
    }

#if EMBEDDED_DESKTOP_VIDEO
    private int _embedFailedPlaybackSession = -1;
    private int _embeddedVlcGeneration = -1;
    private bool _embedFailureLogged;
    private bool _isEmbedded;

    /// <summary>
    /// Set by DesktopHomePage: shows the playback panel and assigns the given
    /// MediaPlayer to the hosted VideoHostView (on the UI thread) so the
    /// drawable is attached before Play. Null (or a failed/timed-out call)
    /// means no hosted surface — standalone-window fallback.
    /// </summary>
    public Func<MediaPlayer, Task>? EmbedSurfaceAsync { get; set; }

    /// <summary>
    /// Raised after a clean stop: the host may now safely clear the
    /// VideoHostView.MediaPlayer binding (the player is idle, not wedged).
    /// </summary>
    public event Action? DetachSurfaceRequested;

    /// <summary>
    /// Raised when a libvlc pair is abandoned as wedged: the host must never
    /// touch that MediaPlayer again — it parks the current VideoHostView
    /// invisible (removing it would run VideoView's drawable-detach against
    /// the wedged player) and builds a fresh host for the next session.
    /// </summary>
    public event Action? SurfacePoisoned;

    /// <summary>True = video renders in-window; false = standalone libvlc window.</summary>
    public event EventHandler<bool>? EmbeddingStateChanged;

    private void SetEmbeddingActive(bool active)
    {
        if (_isEmbedded == active)
        {
            return;
        }

        _isEmbedded = active;
        try
        {
            EmbeddingStateChanged?.Invoke(this, active);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopVideoPlayerService] EmbeddingStateChanged handler failed");
        }
    }

    /// <summary>
    /// Attach the hosted in-window surface for this play, or fall back to the
    /// standalone window for the rest of this playback session. Never throws;
    /// never blocks the UI thread (the host delegate dispatches internally).
    /// </summary>
    private async Task TryEmbedSurfaceAsync(VlcSession vlc)
    {
        if (_embeddedVlcGeneration == vlc.Generation)
        {
            SetEmbeddingActive(true);
            return; // drawable already attached for this player (stream switch)
        }

        var playbackSession = _playbackSessionId;
        string? failure = null;
        if (EmbedSurfaceAsync is not { } embed)
        {
            failure = "no host surface delegate is wired (page not loaded?)";
        }
        else if (_embedFailedPlaybackSession == playbackSession)
        {
            SetEmbeddingActive(false);
            return; // already fell back for this session; stay standalone
        }
        else
        {
            try
            {
                await embed(vlc.Player).WaitAsync(TimeSpan.FromSeconds(3));

                // The native child window is realized on a layout pass after
                // the UI-thread assignment; poll the drawable briefly.
                // (XWindow is a trivial stored-value getter.)
                for (var i = 0; i < 20 && vlc.Player.XWindow == 0 && !vlc.Abandoned; i++)
                {
                    await Task.Delay(100);
                }

                if (!vlc.Abandoned && vlc.Player.XWindow != 0)
                {
                    _embeddedVlcGeneration = vlc.Generation;
                    _logger.LogInformation(
                        "[DesktopVideoPlayerService] In-window surface attached (X drawable 0x{XWindow:X})",
                        vlc.Player.XWindow);
                    SetEmbeddingActive(true);
                    return;
                }

                failure = "the drawable was never attached (handler gap or compositor refusal)";
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        _embedFailedPlaybackSession = playbackSession;
        if (!_embedFailureLogged)
        {
            _embedFailureLogged = true;
            _logger.LogWarning(
                "[DesktopVideoPlayerService] In-window playback unavailable ({Reason}); falling back to the standalone libvlc window for this session",
                failure);
        }
        else
        {
            _logger.LogDebug(
                "[DesktopVideoPlayerService] In-window playback unavailable ({Reason}); standalone-window fallback",
                failure);
        }

        SetEmbeddingActive(false);
    }
#endif

    /// <summary>
    /// Close control. UI-thread safe by construction: completes the playback
    /// session immediately (overlay hides, orchestrator unblocks) and tears
    /// libvlc down on a worker with a timeout — a stop that wedges is
    /// abandoned, never awaited, so Close is always prompt.
    /// </summary>
    public void StopPlayback()
    {
        _logger.LogInformation("[DesktopVideoPlayerService] Close requested (t={Timestamp:HH:mm:ss.fff})", DateTime.UtcNow);
        StopMetricsLoop();
        DisposeRefererProxy();
        PlaybackVisibilityChanged?.Invoke(this, false);
        _playbackTcs?.TrySetResult(new PlaybackResult
        {
            Success = true,
            Message = "User closed playback"
        });

        var vlc = _current;
        if (vlc == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var stopped = await RunVlcOpAsync(vlc, "stop", StopTimeout, () => vlc.Player.Stop());
            _logger.LogInformation(
                "[DesktopVideoPlayerService] Close teardown {Outcome} (t={Timestamp:HH:mm:ss.fff})",
                stopped ? "completed" : "abandoned (libvlc wedged)", DateTime.UtcNow);
#if EMBEDDED_DESKTOP_VIDEO
            _embeddedVlcGeneration = -1;
            if (stopped)
            {
                try
                {
                    DetachSurfaceRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[DesktopVideoPlayerService] DetachSurfaceRequested handler failed");
                }
            }
#endif
        });
    }

    /// <summary>
    /// Runs one libvlc operation on a worker thread, serialized per session,
    /// bounded by <paramref name="timeout"/>. On timeout the session is
    /// abandoned (see <see cref="AbandonSession"/>) and false returns — the
    /// hung op is left running unobserved, per the "abandoned, not awaited"
    /// invariant. Never call libvlc directly from anywhere else.
    /// </summary>
    private async Task<bool> RunVlcOpAsync(VlcSession vlc, string opName, TimeSpan timeout, Action op)
    {
        if (vlc.Abandoned)
        {
            return false;
        }

        var work = Task.Run(async () =>
        {
            if (!await vlc.OpGate.WaitAsync(timeout))
            {
                throw new TimeoutException($"libvlc op gate not acquired for '{opName}'");
            }

            try
            {
                if (vlc.Abandoned)
                {
                    throw new OperationCanceledException("session abandoned");
                }

                op();
            }
            finally
            {
                vlc.OpGate.Release();
            }
        });

        var winner = await Task.WhenAny(work, Task.Delay(timeout));
        if (winner != work)
        {
            AbandonSession(vlc, opName, timeout);
            _ = work.ContinueWith(
                t => _logger.LogWarning(
                    "[DesktopVideoPlayerService] Abandoned libvlc op '{Op}' eventually completed ({Status})",
                    opName, t.Status),
                TaskContinuationOptions.ExecuteSynchronously);
            return false;
        }

        try
        {
            await work;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            AbandonSession(vlc, opName, timeout);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopVideoPlayerService] libvlc op '{Op}' failed", opName);
            return false;
        }
    }

    private void AbandonSession(VlcSession vlc, string opName, TimeSpan timeout)
    {
        if (vlc.Abandoned)
        {
            return;
        }

        vlc.Abandoned = true;
        _logger.LogError(
            "[DesktopVideoPlayerService] libvlc op '{Op}' did not complete within {TimeoutSeconds}s — abandoning this libvlc instance (generation {Generation}); the next play builds a fresh one",
            opName, timeout.TotalSeconds, vlc.Generation);

        if (ReferenceEquals(_current, vlc))
        {
            _current = null;
        }

#if EMBEDDED_DESKTOP_VIDEO
        if (_embeddedVlcGeneration == vlc.Generation)
        {
            _embeddedVlcGeneration = -1;
        }

        try
        {
            SurfacePoisoned?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopVideoPlayerService] SurfacePoisoned handler failed");
        }
#endif
    }

    /// <summary>
    /// Conservative libvlc options are the WSL default (field failure: a
    /// wedged hardware/vout probe froze playback) and the
    /// VARDYPARTY_DESKTOP_VLC_SAFE=1 override; also safe under xvfb.
    /// </summary>
    private static bool UseConservativeVlcOptions => DesktopPlatformProbe.UseConservativeVlcOptions;

    private string[] BuildVlcOptions()
    {
        var options = DesktopPlatformProbe.BuildLibVlcOptions();
        if (UseConservativeVlcOptions)
        {
            _logger.LogInformation(
                "[DesktopVideoPlayerService] Conservative libvlc options active (WSL={IsWsl}, forced={Forced}): software decode, plain X11 vout, aout={Aout}",
                DesktopPlatformProbe.IsWsl, DesktopPlatformProbe.ForceSafeVlcOptions,
                DesktopPlatformProbe.ResolveAudioOutputModule());
        }
        else
        {
            _logger.LogInformation(
                "[DesktopVideoPlayerService] libvlc aout={Aout}",
                DesktopPlatformProbe.ResolveAudioOutputModule());
        }

        return options;
    }

    /// <summary>
    /// Unmute + volume only. Do NOT call <c>SetAudioOutput</c> here: aout is
    /// already pinned via <c>--aout=</c> in <see cref="BuildVlcOptions"/>.
    /// Calling SetAudioOutput before Play loads pulse early; the mandatory
    /// Stop() before a new Media then logs "removing module pulse" and has
    /// cancelled the adaptive demuxer on WSL (Cancellation 0x8).
    /// </summary>
    private static void ConfigureAudioOutput(MediaPlayer player)
    {
        try
        {
            player.Mute = false;
            player.Volume = 100;
        }
        catch
        {
        }
    }

    /// <summary>
    /// Lazy libvlc bring-up on a worker op; returns null (never throws) when
    /// libvlc is unavailable or initialisation wedged. A previously abandoned
    /// pair is left behind and a fresh one is built.
    /// </summary>
    private async Task<VlcSession?> EnsureSessionAsync()
    {
        var existing = _current;
        if (existing is { Abandoned: false })
        {
            return existing;
        }

        if (_initFailed)
        {
            return null;
        }

        await _ensureLock.WaitAsync();
        try
        {
            existing = _current;
            if (existing is { Abandoned: false })
            {
                return existing;
            }

            if (_initFailed)
            {
                return null;
            }

            var generation = ++_nextVlcGeneration;
            VlcSession? created = null;
            var initTask = Task.Run(() =>
            {
                Core.Initialize();
                var libVlc = new LibVLC(BuildVlcOptions());
                var player = new MediaPlayer(libVlc);
                ConfigureAudioOutput(player);
                created = new VlcSession { Generation = generation, LibVlc = libVlc, Player = player };
            });

            var winner = await Task.WhenAny(initTask, Task.Delay(InitTimeout));
            if (winner != initTask)
            {
                _initFailed = true;
                _logger.LogError(
                    "[DesktopVideoPlayerService] LibVLC initialisation did not complete within {TimeoutSeconds}s — abandoning it (playback disabled for this run)",
                    InitTimeout.TotalSeconds);
                return null;
            }

            try
            {
                await initTask;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                _logger.LogError(ex,
                    "[DesktopVideoPlayerService] Failed to initialize LibVLC — is the system libvlc installed (e.g. apt install vlc)?");
                return null;
            }

            var vlc = created!;
            AttachLibVlcDiagnostics(vlc);
            vlc.Player.Playing += OnPlaying;
            vlc.Player.Buffering += OnBuffering;
            vlc.Player.EncounteredError += OnEncounteredError;
            vlc.Player.EndReached += OnEndReached;

            _current = vlc;
            _logger.LogInformation(
                "[DesktopVideoPlayerService] LibVLC initialized successfully (generation {Generation})", generation);
            return vlc;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    public async Task<PlaybackResult> PlayVideoAsync(
        // ReSharper disable once InconsistentNaming
        string m3u8Url,
        string refererUrl,
        string title,
        Func<Task>? onNextStreamRequested = null,
        string? league = null,
        string? homeTeam = null,
        string? awayTeam = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        _logger.LogInformation("[DesktopVideoPlayerService] Playing video: {Title}", title);
        _logger.LogInformation("[DesktopVideoPlayerService] URL: {Url}", m3u8Url);
        _logger.LogInformation("[DesktopVideoPlayerService] Referer: {Referer}", refererUrl);

        _playbackSessionId++;

        if (await EnsureSessionAsync() == null)
        {
            return new PlaybackResult
            {
                Success = false,
                Message = "Video playback is unavailable: libvlc could not be initialized. Install VLC (libvlc) and try again."
            };
        }

        _onNextStreamRequested = onNextStreamRequested;
        _playbackTcs = new TaskCompletionSource<PlaybackResult>();
        _refererUrl = refererUrl;
        _requestHeaders = requestHeaders;

        try
        {
            _session.Reset();
            AttachViaSession(m3u8Url);
            var result = await _playbackTcs.Task;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DesktopVideoPlayerService] Error during playback");
            PlaybackVisibilityChanged?.Invoke(this, false);
            return new PlaybackResult
            {
                Success = false,
                Message = $"Playback error: {ex.Message}"
            };
        }
    }

    private void DispatchEngine(MediaEngineEvent engineEvent)
    {
        try
        {
            ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.Handle(engineEvent)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopVideoPlayerService] DispatchEngine failed ({Kind})", engineEvent.Kind);
        }
    }

    private void AttachViaSession(string url, bool usedCachedUrl = false, bool force = false)
    {
        _session.SetHealthyStreamCount(_switching.GetHealthyStreams().Count);
        ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.BeginAttach(url, usedCachedUrl, force)));
    }

    private void ApplyPlaybackCommand(PlaybackCommand cmd)
    {
        PlaybackCommandExecutor.Apply(cmd, new DesktopPlaybackCommandHost(this));
    }

    private sealed class DesktopPlaybackCommandHost(DesktopVideoPlayerService player) : IPlaybackCommandHost
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
            player._logger.LogWarning("[DesktopVideoPlayerService] Stream failed: {Reason}", reason);
            _ = player._healthReporter.ReportPlaybackErrorAsync(
                player._session.Snapshot.CurrentUrl, player._refererUrl, error: reason);
        }

        public void ReportDeclined(string? reason)
        {
            player._logger.LogWarning("[DesktopVideoPlayerService] Stream declined: {Reason}", reason);
            _ = player._healthReporter.ReportPlaybackErrorAsync(
                player._session.Snapshot.CurrentUrl, player._refererUrl, error: reason);
        }

        public void ReportWorking()
        {
            player._logger.LogInformation("[DesktopVideoPlayerService] Stream established");
            _ = player._healthReporter.ReportPlaybackStartedAsync(
                player._session.Snapshot.CurrentUrl,
                player._refererUrl,
                metrics: player.GetCurrentMetrics());
        }

        public void MarkEstablished()
        {
            // Session established flag is owned by PlaybackSessionController.Handle(Ready).
        }

        public void RaiseBuffering(bool isBuffering)
        {
            player.BufferingStateChanged?.Invoke(player, isBuffering);
            if (isBuffering)
            {
                _ = player._healthReporter.ReportBufferingAsync(
                    player._session.Snapshot.CurrentUrl,
                    player._refererUrl,
                    metrics: player.GetCurrentMetrics());
            }
        }

        public void Attach(string url, bool isRevert)
        {
            if (isRevert)
                player._logger.LogWarning("[DesktopVideoPlayerService] Reverting to last good stream: {Url}", url);
            _ = player._engine.AttachAsync(url, player._requestHeaders);
        }

        public void AttachCurrentAfterRemove() => _ = player._pool.AttachCurrentFromPoolAsync();

        public void RetryFreshResolve() => _ = player._pool.RetryFreshResolveAsync();

        /// <summary>Off-thread with timeout — command dispatch may run on a libvlc event thread (reentrant Stop deadlocks libvlc) or the UI thread.</summary>
        public void StopEngine()
        {
            if (player._current is { Abandoned: false } vlc)
            {
                _ = player.RunVlcOpAsync(vlc, "stop-engine", StopTimeout, () => vlc.Player.Stop());
            }
        }

        public void CloseSession(string reason)
        {
            player.PlaybackVisibilityChanged?.Invoke(player, false);
            player._playbackTcs?.TrySetResult(PlaybackResult.Completed(reason, true));
        }

        public void SwitchPoolToNext()
        {
            if (player._onNextStreamRequested != null)
                _ = player._onNextStreamRequested();
        }

        public void SwitchPoolToPrevious()
        {
            player._pool.SwitchPoolToPrevious();
            _ = player._pool.AttachCurrentFromPoolAsync();
        }

        public void NotifyApplyFailed(Exception exception)
            => player._logger.LogWarning(exception, "[DesktopVideoPlayerService] ApplyPlaybackCommand failed");
    }

    private async Task AttachLibVlcAsync(
        string m3u8Url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken)
    {
        var generation = _session.Snapshot.AttachGeneration;
        var vlc = await EnsureSessionAsync();
        if (vlc == null)
        {
            _engine.Raise(MediaEngineEvent.Error(generation, "LibVLC is not initialized"));
            return;
        }

        // Show the in-app playback surface first (hosts dispatch internally),
        // then attach the drawable, then play — the drawable must be set
        // before the vout is created or libvlc opens its own window anyway.
        PlaybackVisibilityChanged?.Invoke(this, true);
#if EMBEDDED_DESKTOP_VIDEO
        await TryEmbedSurfaceAsync(vlc);
#endif

        var referer = _refererUrl;
        string playUrl;
        try
        {
            playUrl = await EnsureRefererBridgeAsync(m3u8Url, referer, requestHeaders, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[DesktopVideoPlayerService] Referer bridge failed; LibVLC will open the CDN URL directly");
            playUrl = m3u8Url;
        }

        var bridged = !string.Equals(playUrl, m3u8Url, StringComparison.Ordinal);
        var attached = await RunVlcOpAsync(vlc, "attach-play", AttachTimeout, () =>
        {
            var previousMedia = _currentMedia;
            _currentMedia = null;

            // Stop only when something is actually loaded — Stop() on a fresh
            // MediaPlayer tears down aout ("removing module pulse") and has
            // raced the next Play's adaptive demux on WSL.
            if (previousMedia != null || vlc.Player.IsPlaying || vlc.Player.Media != null)
            {
                vlc.Player.Stop();
            }

            previousMedia?.Dispose();

            var media = new Media(vlc.LibVlc, new Uri(playUrl));
            // Direct CDN play still needs Referer; the local bridge injects it
            // itself and LibVLC only talks to 127.0.0.1.
            if (!bridged && !string.IsNullOrWhiteSpace(referer))
                media.AddOption($":http-referrer={referer}");

            media.AddOption(":http-user-agent=Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            media.AddOption(UseConservativeVlcOptions ? ":avcodec-hw=none" : ":avcodec-hw=any");
            media.AddOption(":network-caching=3000");

            ConfigureAudioOutput(vlc.Player);
            _currentMedia = media;
            vlc.Player.Play(media);
        });

        if (!attached)
        {
            _engine.Raise(MediaEngineEvent.Error(generation, "libvlc attach did not complete (instance abandoned)"));
            return;
        }

        _logger.LogInformation(
            "[DesktopVideoPlayerService] Requested LibVLC attach for {Url} (bridged={Bridged})",
            playUrl, bridged);
    }

    private async Task<string> EnsureRefererBridgeAsync(
        string m3u8Url,
        string? referer,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken)
    {
        _refererProxy ??= new LibVlcRefererProxy(
            _httpClientFactory.CreateClient(PlaybackHttpClients.LibVlcBridge),
            _logger);

        return await _refererProxy.BindAsync(m3u8Url, referer, requestHeaders, cancellationToken);
    }

    private void DisposeRefererProxy()
    {
        var proxy = Interlocked.Exchange(ref _refererProxy, null);
        if (proxy == null)
        {
            return;
        }

        _ = proxy.DisposeAsync().AsTask().ContinueWith(
            t => _logger.LogDebug(t.Exception, "[DesktopVideoPlayerService] Referer proxy dispose faulted"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>Player events fire on libvlc threads; ignore anything from a superseded/abandoned player.</summary>
    private bool IsCurrentPlayer(object? sender) =>
        _current is { Abandoned: false } vlc && ReferenceEquals(vlc.Player, sender);

    private void OnPlaying(object? sender, EventArgs e)
    {
        if (!IsCurrentPlayer(sender))
        {
            return;
        }

        _logger.LogInformation("[DesktopVideoPlayerService] Playback started");
        PlaybackVisibilityChanged?.Invoke(this, true);
        SetBufferingState(false);
        _engine.Raise(MediaEngineEvent.Ready(_session.Snapshot.AttachGeneration));
        StartMetricsLoop();
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        if (!IsCurrentPlayer(sender))
        {
            return;
        }

        var isBuffering = e.Cache < 100f;
        _logger.LogDebug("[DesktopVideoPlayerService] Buffering: {Percentage}%", e.Cache);
        SetBufferingState(isBuffering);
        _engine.Raise(MediaEngineEvent.Buffering(_session.Snapshot.AttachGeneration, isBuffering));
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        if (!IsCurrentPlayer(sender))
        {
            return;
        }

        _logger.LogError("[DesktopVideoPlayerService] Playback error encountered");
        StopMetricsLoop();
        _engine.Raise(MediaEngineEvent.Error(_session.Snapshot.AttachGeneration, "Stream playback failed"));
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (!IsCurrentPlayer(sender))
        {
            return;
        }

        _logger.LogInformation("[DesktopVideoPlayerService] Playback ended");
        StopMetricsLoop();
        _engine.Raise(MediaEngineEvent.Ended(_session.Snapshot.AttachGeneration));
        PlaybackVisibilityChanged?.Invoke(this, false);
        _playbackTcs?.TrySetResult(PlaybackResult.SuccessResult("Playback completed"));
    }

    private void SetBufferingState(bool isBuffering)
    {
        if (_isBuffering != isBuffering)
        {
            _isBuffering = isBuffering;
            BufferingStateChanged?.Invoke(this, isBuffering);
        }
    }

    public PlaybackMetrics? GetCurrentMetrics()
    {
        var vlc = _current;
        if (vlc is not { Abandoned: false } || !vlc.Player.IsPlaying)
        {
            return null;
        }

        try
        {
            var media = vlc.Player.Media;
            if (media == null)
            {
                return null;
            }

            var videoTrack = vlc.Player.VideoTrack;
            if (videoTrack <= 0)
            {
                return null;
            }

            // LibVLC doesn't directly expose resolution/framerate during playback.
            return new PlaybackMetrics
            {
                Resolution = null,
                Framerate = null,
                VideoCodec = "H.264",
                AudioCodec = null,
                BitrateKbps = null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopVideoPlayerService] Error getting playback metrics");
            return null;
        }
    }

    private void StartMetricsLoop()
    {
        StopMetricsLoop();
        _metricsTimer = new Timer(_ =>
        {
            try
            {
                var metrics = GetCurrentMetrics();
                _engine.Raise(MediaEngineEvent.Metrics(
                    _session.Snapshot.AttachGeneration,
                    metrics?.BitrateKbps,
                    _isBuffering));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DesktopVideoPlayerService] Metrics raise failed");
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void StopMetricsLoop()
    {
        _metricsTimer?.Dispose();
        _metricsTimer = null;
    }

    /// <summary>
    /// App shutdown. Teardown runs as a bounded worker op like everything
    /// else; an abandoned (wedged) pair is skipped entirely — the process is
    /// exiting and a hung dispose would stall shutdown.
    /// </summary>
    public void Dispose()
    {
        _logger.LogInformation("[DesktopVideoPlayerService] Disposing");
        StopMetricsLoop();
        DisposeRefererProxy();

        var vlc = _current;
        _current = null;
        if (vlc is { Abandoned: false })
        {
            var disposeTask = RunVlcOpAsync(vlc, "dispose", DisposeTimeout, () =>
            {
                vlc.Player.Playing -= OnPlaying;
                vlc.Player.Buffering -= OnBuffering;
                vlc.Player.EncounteredError -= OnEncounteredError;
                vlc.Player.EndReached -= OnEndReached;
                vlc.Player.Stop();
                vlc.Player.Dispose();

                _currentMedia?.Dispose();
                _currentMedia = null;

                DetachLibVlcDiagnostics(vlc);
                vlc.LibVlc.Dispose();
            });

            try
            {
                // Bounded by DisposeTimeout inside RunVlcOpAsync; a wedged
                // dispose is abandoned there rather than stalling shutdown.
                disposeTask.Wait(DisposeTimeout + TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DesktopVideoPlayerService] Error during disposal");
            }
        }

        PlaybackVisibilityChanged?.Invoke(this, false);
        GC.SuppressFinalize(this);
    }

    private void AttachLibVlcDiagnostics(VlcSession vlc)
    {
        if (vlc.LogHandler != null)
        {
            return;
        }

        vlc.LogHandler = (_, logEventArgs) =>
        {
            try
            {
                var message = logEventArgs.Message?.Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    return;
                }

                var module = logEventArgs.Module?.Trim();
                var level = logEventArgs.Level.ToString();
                var renderedMessage = string.IsNullOrWhiteSpace(module)
                    ? $"[LibVLC:{level}] {message}"
                    : $"[LibVLC:{level}:{module}] {message}";

                if (IsLibVlcErrorLevel(level))
                {
                    _logger.LogError("[DesktopVideoPlayerService] {Message}", renderedMessage);
                    if (IsFatalAdaptiveDemuxFailure(module, message))
                    {
                        RaiseDemuxFailureOnce("LibVLC adaptive demux failed (segment not playable)");
                    }

                    return;
                }

                if (IsLibVlcWarningLevel(level))
                {
                    _logger.LogWarning("[DesktopVideoPlayerService] {Message}", renderedMessage);
                    return;
                }

                if (IsRenderDiagnosticInteresting(message))
                {
                    _logger.LogInformation("[DesktopVideoPlayerService] {Message}", renderedMessage);
                }
            }
            catch
            {
            }
        };

        vlc.LibVlc.Log += vlc.LogHandler;
        _logger.LogInformation("[DesktopVideoPlayerService] LibVLC native diagnostics enabled");
    }

    private static void DetachLibVlcDiagnostics(VlcSession vlc)
    {
        if (vlc.LogHandler == null)
        {
            return;
        }

        try
        {
            vlc.LibVlc.Log -= vlc.LogHandler;
        }
        catch
        {
        }

        vlc.LogHandler = null;
    }

    private static bool IsLibVlcErrorLevel(string level)
    {
        return level.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               level.Contains("crit", StringComparison.OrdinalIgnoreCase) ||
               level.Contains("alert", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLibVlcWarningLevel(string level)
    {
        return level.Contains("warn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRenderDiagnosticInteresting(string message)
    {
        return message.Contains("egl", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("mesa", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("zink", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("vout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("aout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("pulse", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("alsa", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("decoder", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("avcodec", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("drm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFatalAdaptiveDemuxFailure(string? module, string message) =>
        (string.IsNullOrEmpty(module) || module.Contains("adaptive", StringComparison.OrdinalIgnoreCase)) &&
        message.Contains("Failed to create demuxer", StringComparison.OrdinalIgnoreCase);

    private void RaiseDemuxFailureOnce(string reason)
    {
        var generation = _session.Snapshot.AttachGeneration;
        if (Interlocked.Exchange(ref _demuxFailureGeneration, generation) == generation)
        {
            return;
        }

        _logger.LogWarning("[DesktopVideoPlayerService] Treating demux failure as stream error: {Reason}", reason);
        _engine.Raise(MediaEngineEvent.Error(generation, reason));
    }
}
