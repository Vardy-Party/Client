using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Linux.Services;

/// <summary>
/// LibVLC playback for the Linux head.
///
/// Rendering surface: with the EmbeddedLinuxVideo build switch ON (the
/// default), playback composites INSIDE the app window — LinuxHomePage
/// hosts a VideoHostView (Avalonia Image) and hands this service an
/// <see cref="AcquireFramePresenterAsync"/> delegate. LibVLC software
/// callbacks (RV32) paint that Image so MAUI chrome can overlay the
/// picture. If the presenter cannot be acquired the service logs once and
/// falls back — for that playback session — to the pre-feature behaviour:
/// no callbacks attached, libvlc opens its own native video window. With
/// the switch OFF the standalone-window path is the only one compiled in.
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
/// VARDYPARTY_LINUX_VLC_SAFE=1 — libvlc gets conservative options:
/// software decode, Pulse aout (WSLg), no hardware probing. In-window
/// compositing uses <c>--vout=vmem</c> (never <c>--vout=x11</c>, which
/// fights the callback path). Standalone fallback still pins X11 vout.
/// Safe under xvfb too. Audio is never disabled (<c>--no-audio</c> is not
/// an option); see <see cref="LinuxPlatformProbe.BuildLibVlcOptions"/>.
/// SoundFlow yields the Pulse device before Play so libvlc can own it.
///
/// LibVLC initialisation is lazy (first PlayVideoAsync), never in the startup
/// path: machines without libvlc installed (or headless CI) get a logged
/// playback error instead of a startup crash.
/// </summary>
public class LinuxVideoPlayerService : INativeVideoPlayerService, IDisposable
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Pulse/PipeWire settle after SoundFlow yield before LibVLC opens pulse.</summary>
    private static readonly TimeSpan UiSoundHandoffSettle = TimeSpan.FromMilliseconds(75);

    private readonly ILogger<LinuxVideoPlayerService> _logger;
    private readonly IStreamSwitchingService _switching;
    private readonly IStreamHealthReporter _healthReporter;
    private readonly LinuxHlsLocalProxy? _hlsProxy;
    private readonly IPlaybackPlaylistProcessor? _playlistProcessor;
    private readonly PlaybackSessionController _session = new();
    private readonly DelegatingMediaEngine _engine = new();
    private readonly PlaybackPoolCommandActions _pool;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly IDisposable _indexSubscription;
    private VlcSession? _current;
    private int _nextVlcGeneration;
    private Media? _currentMedia;
    private long _demuxFailureGeneration = -1;
    private TaskCompletionSource<PlaybackResult>? _playbackTcs;
    private Func<Task>? _onNextStreamRequested;
    private bool _isBuffering;
    private float _bufferCachePercent;
    private bool _initFailed;
    private string? _refererUrl;
    private IReadOnlyDictionary<string, string>? _requestHeaders;
    private Timer? _metricsTimer;
    private int _playbackSessionId;
    private string? _playbackTitle;
    private string? _playbackLeague;
    private string? _playbackHomeTeam;
    private string? _playbackAwayTeam;
    private bool _isNextStreamRequestInProgress;
    /// <summary>
    /// When true, <see cref="CurrentStreamIndexChanged"/> must not re-attach —
    /// the session command path already owns Attach (same as Windows/Android).
    /// </summary>
    private bool _suppressIndexDrivenSwitch;

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
        public LibVlcSoftwareFrameSink? FrameSink;
    }

    public LinuxVideoPlayerService(
        ILogger<LinuxVideoPlayerService> logger,
        IStreamSwitchingService switching,
        ResolveFreshPlaybackUrlAsync resolveFresh,
        IStreamHealthReporter healthReporter,
        IHttpClientFactory? httpClientFactory = null,
        IPlaybackPlaylistProcessor? playlistProcessor = null)
    {
        _logger = logger;
        _switching = switching;
        _healthReporter = healthReporter;
        _playlistProcessor = playlistProcessor;
        if (httpClientFactory is not null)
        {
            _hlsProxy = new LinuxHlsLocalProxy(
                httpClientFactory.CreateClient(VardyParty.Hosting.PlaybackHttpClients.Media),
                logger);
            if (_playlistProcessor is not null)
                _hlsProxy.SetPlaylistTransform(_playlistProcessor.Process);
        }
        _pool = new PlaybackPoolCommandActions(
            _session,
            _switching,
            resolveFresh,
            AttachViaSession,
            ApplyPlaybackCommand);
        _engine.EngineEvent += (_, engineEvent) => DispatchEngine(engineEvent);
        _engine.MetricsHandler = GetCurrentMetrics;
        _engine.AttachHandler = AttachLibVlcAsync;
        // Orchestrator Next only advances the pool index; Windows/Android re-attach
        // from this subscription. Without it Linux updates Stream N/M chrome and
        // leaves LibVLC on the previous URL (audio blip then black player).
        _indexSubscription = _switching.CurrentStreamIndexChanged.Subscribe(_ =>
            TrySwitchToCurrentStream());
    }

#if EMBEDDED_LINUX_VIDEO
    private int _embedFailedPlaybackSession = -1;
    private int _embeddedVlcGeneration = -1;
    private bool _embedFailureLogged;
    private bool _isEmbedded;

    /// <summary>
    /// Set by LinuxHomePage: shows the playback panel and returns the
    /// hosted <see cref="IVideoFramePresenter"/> (UI thread) so software
    /// callbacks can be attached before Play. Null (or a failed/timed-out
    /// call) means no composited surface — standalone-window fallback.
    /// </summary>
    public Func<Task<IVideoFramePresenter?>>? AcquireFramePresenterAsync { get; set; }

    /// <summary>
    /// Raised after a clean stop: the host may clear the composited frame.
    /// </summary>
    public event Action? DetachSurfaceRequested;

    /// <summary>
    /// Raised when a libvlc pair is abandoned as wedged: the host parks the
    /// current VideoHostView and builds a fresh host for the next session.
    /// The Image path does not detach a native drawable, but a new presenter
    /// still avoids presenting into a poisoned generation.
    /// </summary>
    public event Action? SurfacePoisoned;

    /// <summary>True = video composites in-window; false = standalone libvlc window.</summary>
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
            _logger.LogDebug(ex, "[LinuxVideoPlayerService] EmbeddingStateChanged handler failed");
        }
    }

    /// <summary>
    /// Acquire the composited Avalonia presenter and attach software-frame
    /// callbacks, or fall back to the standalone window for the rest of this
    /// playback session. Never throws; never blocks the UI thread (the host
    /// delegate dispatches internally). Does not poll XWindow — callback
    /// vout has no native drawable.
    /// </summary>
    private async Task TryEmbedSurfaceAsync(VlcSession vlc)
    {
        if (_embeddedVlcGeneration == vlc.Generation)
        {
            SetEmbeddingActive(true);
            return; // callbacks already attached for this player (stream switch)
        }

        var playbackSession = _playbackSessionId;
        string? failure = null;
        if (AcquireFramePresenterAsync is not { } acquire)
        {
            failure = "no frame presenter delegate is wired (page not loaded?)";
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
                var presenter = await acquire().WaitAsync(TimeSpan.FromSeconds(3));
                if (presenter == null)
                {
                    failure = "the host did not return a frame presenter";
                }
                else
                {
                    var attached = await RunVlcOpAsync(vlc, "attach-frame-sink", AttachTimeout, () =>
                    {
                        vlc.FrameSink?.Dispose();
                        var present = LinuxPlatformProbe.ResolveSoftwarePresentLimits(
                            UseConservativeVlcOptions);
                        var sink = new LibVlcSoftwareFrameSink(
                            presenter,
                            maxFrameWidth: present.MaxFrameWidth,
                            minPresentIntervalMs: present.MinPresentIntervalMs);
                        sink.Attach(vlc.Player);
                        vlc.FrameSink = sink;
                    });

                    if (attached && !vlc.Abandoned)
                    {
                        _embeddedVlcGeneration = vlc.Generation;
                        _logger.LogInformation(
                            "[LinuxVideoPlayerService] Composited frame sink attached (generation {Generation})",
                            vlc.Generation);
                        SetEmbeddingActive(true);
                        return;
                    }

                    failure = attached
                        ? "the libvlc session was abandoned while attaching the frame sink"
                        : "attaching the software-frame sink did not complete";
                }
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
                "[LinuxVideoPlayerService] In-window compositing unavailable ({Reason}); falling back to the standalone libvlc window for this session",
                failure);
        }
        else
        {
            _logger.LogDebug(
                "[LinuxVideoPlayerService] In-window compositing unavailable ({Reason}); standalone-window fallback",
                failure);
        }

        SetEmbeddingActive(false);
    }
#endif

    /// <summary>Game title shown in the video-info panel (Home vs Away).</summary>
    public string? PlaybackTitle => _playbackTitle;

    /// <summary>
    /// Latest LibVLC buffering cache percent (0–100). Updated from Buffering events;
    /// stays at the last value between events (typically 100 while stably playing).
    /// </summary>
    public int? BufferPercent =>
        _bufferCachePercent > 0 || _isBuffering
            ? (int)Math.Clamp(Math.Round(_bufferCachePercent), 0, 100)
            : null;

    public string? PlaybackLeague => _playbackLeague;

    public string? PlaybackHomeTeam => _playbackHomeTeam;

    public string? PlaybackAwayTeam => _playbackAwayTeam;

    /// <summary>
    /// Invokes the orchestrator-supplied next-stream callback (same path as
    /// session SwitchPoolToNext). Used by the Avalonia playback chrome.
    /// </summary>
    public async Task RequestNextStreamAsync()
    {
        if (_onNextStreamRequested is null || _isNextStreamRequestInProgress)
            return;

        _isNextStreamRequestInProgress = true;
        try
        {
            await _onNextStreamRequested();
        }
        finally
        {
            _isNextStreamRequestInProgress = false;
        }
    }

    /// <summary>User Previous — pool rotate; index subscription attaches the URL.</summary>
    public void RequestPreviousStream()
    {
        if (_switching.GetHealthyStreams().Count <= 1)
        {
            return;
        }

        _pool.SwitchPoolToPrevious();
    }

    /// <summary>
    /// Index-driven attach after Next/Previous pool rotate (Windows
    /// <c>TrySwitchToCurrentStreamAsync</c> / Android <c>TrySwitchToCurrentStream</c>).
    /// </summary>
    private void TrySwitchToCurrentStream(bool force = false)
    {
        if (_suppressIndexDrivenSwitch)
        {
            return;
        }

        // First healthy chip publishes index 0 before PlayVideoAsync runs — attaching
        // here races the orchestrator attach (Stop tears pulse, second Play fails).
        // Only follow index changes while a PlayVideoAsync session is active.
        if (_playbackTcs is null)
        {
            return;
        }

        try
        {
            var current = _switching.GetCurrentStream();
            var url = current?.ResolvedM3U8Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (!force &&
                string.Equals(_session.Snapshot.CurrentUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // LibVLC often never raises Playing on the vmem/HLS path, so the
            // session stays IsPreparing and CanAttach rejects the new URL.
            // Index-driven switches already compared URLs — force the attach.
            _logger.LogInformation(
                "[LinuxVideoPlayerService] Switching playback source (force={Force}) to {Channel}",
                true,
                current?.Stream?.Channel ?? "(unknown)");
            _requestHeaders = current?.RequestHeaders ?? _requestHeaders;
            if (!string.IsNullOrWhiteSpace(current?.Referer))
                _refererUrl = current.Referer;
            AttachViaSession(url, usedCachedUrl: false, force: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LinuxVideoPlayerService] Stream switch failed");
        }
    }

    /// <summary>
    /// Close control. UI-thread safe by construction: completes the playback
    /// session immediately (overlay hides, orchestrator unblocks) and tears
    /// libvlc down on a worker with a timeout — a stop that wedges is
    /// abandoned, never awaited, so Close is always prompt.
    /// </summary>
    public void StopPlayback()
    {
        _logger.LogInformation("[LinuxVideoPlayerService] Close requested (t={Timestamp:HH:mm:ss.fff})", DateTime.UtcNow);
        StopMetricsLoop();
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
                "[LinuxVideoPlayerService] Close teardown {Outcome} (t={Timestamp:HH:mm:ss.fff})",
                stopped ? "completed" : "abandoned (libvlc wedged)", DateTime.UtcNow);
#if EMBEDDED_LINUX_VIDEO
            _embeddedVlcGeneration = -1;
            if (stopped)
            {
                try
                {
                    DetachSurfaceRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[LinuxVideoPlayerService] DetachSurfaceRequested handler failed");
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
                    "[LinuxVideoPlayerService] Abandoned libvlc op '{Op}' eventually completed ({Status})",
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
            _logger.LogWarning(ex, "[LinuxVideoPlayerService] libvlc op '{Op}' failed", opName);
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
            "[LinuxVideoPlayerService] libvlc op '{Op}' did not complete within {TimeoutSeconds}s — abandoning this libvlc instance (generation {Generation}); the next play builds a fresh one",
            opName, timeout.TotalSeconds, vlc.Generation);

        if (ReferenceEquals(_current, vlc))
        {
            _current = null;
        }

#if EMBEDDED_LINUX_VIDEO
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
            _logger.LogDebug(ex, "[LinuxVideoPlayerService] SurfacePoisoned handler failed");
        }
#endif
    }

    /// <summary>
    /// Conservative libvlc options are the WSL default (field failure: a
    /// wedged hardware/vout probe froze playback) and the
    /// VARDYPARTY_LINUX_VLC_SAFE=1 override; also safe under xvfb.
    /// </summary>
    private static bool UseConservativeVlcOptions => LinuxPlatformProbe.UseConservativeVlcOptions;

    private string[] BuildVlcOptions()
    {
#if EMBEDDED_LINUX_VIDEO
        var options = LinuxPlatformProbe.BuildLibVlcOptions(
            UseConservativeVlcOptions,
            Environment.GetEnvironmentVariable(LinuxPlatformProbe.AudioOutputVariableName),
            callbackVout: true);
#else
        var options = LinuxPlatformProbe.BuildLibVlcOptions();
#endif
        var aout = LinuxPlatformProbe.ResolveAudioOutputModule();
        if (UseConservativeVlcOptions)
        {
            _logger.LogInformation(
                "[LinuxVideoPlayerService] Conservative libvlc options active (WSL={IsWsl}, forced={Forced}): software decode, vout={Vout}, aout={Aout}; {AudioEnv}",
                LinuxPlatformProbe.IsWsl, LinuxPlatformProbe.ForceSafeVlcOptions,
#if EMBEDDED_LINUX_VIDEO
                "vmem/callbacks",
#else
                "x11",
#endif
                aout, LinuxPlatformProbe.DescribeAudioEnvironment());
        }
        else
        {
            _logger.LogInformation(
                "[LinuxVideoPlayerService] libvlc aout={Aout} vout={Vout}; {AudioEnv}",
                aout,
#if EMBEDDED_LINUX_VIDEO
                "vmem/callbacks",
#else
                "default",
#endif
                LinuxPlatformProbe.DescribeAudioEnvironment());
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
    /// After Playing: keep unmuted, and if no audio track is selected pick the
    /// first real track (Id &gt;= 0). LibVLC occasionally starts with track -1
    /// (silent picture) until the user toggles audio.
    /// </summary>
    private void EnsurePlaybackAudio(MediaPlayer player)
    {
        try
        {
            player.Mute = false;
            player.Volume = 100;

            var selected = player.AudioTrack;
            var descriptions = player.AudioTrackDescription;
            if (selected >= 0 || descriptions is null || descriptions.Length == 0)
            {
                _logger.LogInformation(
                    "[LinuxVideoPlayerService] Playback audio state: mute={Mute}, volume={Volume}, audioTrack={Track}, tracks={TrackCount}",
                    player.Mute, player.Volume, selected, descriptions?.Length ?? 0);
                return;
            }

            TrackDescription? first = null;
            foreach (var track in descriptions)
            {
                if (track.Id >= 0)
                {
                    first = track;
                    break;
                }
            }

            if (first is null)
            {
                _logger.LogWarning(
                    "[LinuxVideoPlayerService] Playback has no selectable audio track (mute={Mute}, volume={Volume})",
                    player.Mute, player.Volume);
                return;
            }

            if (!player.SetAudioTrack(first.Value.Id))
            {
                _logger.LogWarning(
                    "[LinuxVideoPlayerService] SetAudioTrack({TrackId}) failed; mute={Mute}, volume={Volume}",
                    first.Value.Id, player.Mute, player.Volume);
                return;
            }

            _logger.LogInformation(
                "[LinuxVideoPlayerService] Selected audio track {TrackId} ({Name}); mute={Mute}, volume={Volume}",
                first.Value.Id, first.Value.Name, player.Mute, player.Volume);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxVideoPlayerService] EnsurePlaybackAudio failed");
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
                LinuxPlatformProbe.TryApplyPulseLatencyHint();
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
                    "[LinuxVideoPlayerService] LibVLC initialisation did not complete within {TimeoutSeconds}s — abandoning it (playback disabled for this run)",
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
                    "[LinuxVideoPlayerService] Failed to initialize LibVLC — is the system libvlc installed (e.g. apt install vlc)?");
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
                "[LinuxVideoPlayerService] LibVLC initialized successfully (generation {Generation})", generation);
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
        _logger.LogInformation("[LinuxVideoPlayerService] Playing video: {Title}", title);
        _logger.LogInformation("[LinuxVideoPlayerService] URL: {Url}", m3u8Url);
        _logger.LogInformation("[LinuxVideoPlayerService] Referer: {Referer}", refererUrl);

        _playbackSessionId++;
        _playbackTitle = title;
        _playbackLeague = league;
        _playbackHomeTeam = homeTeam;
        _playbackAwayTeam = awayTeam;

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
            _logger.LogError(ex, "[LinuxVideoPlayerService] Error during playback");
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
            _logger.LogWarning(ex, "[LinuxVideoPlayerService] DispatchEngine failed ({Kind})", engineEvent.Kind);
        }
    }

    private void AttachViaSession(string url, bool usedCachedUrl = false, bool force = false)
    {
        _session.SetHealthyStreamCount(_switching.GetHealthyStreams().Count);
        var effects = _session.BeginAttach(url, usedCachedUrl, force);
        if (effects.Count > 0 && effects[0].Kind == PlaybackEffectKind.None)
        {
            _logger.LogWarning(
                "[LinuxVideoPlayerService] Attach rejected ({Reason}) url={Url} preparing={Preparing}",
                effects[0].Reason,
                url,
                _session.Snapshot.IsPreparing);
        }

        ApplyPlaybackCommand(PlaybackCommand.FromEffects(effects));
    }

    private void ApplyPlaybackCommand(PlaybackCommand cmd)
    {
        PlaybackCommandExecutor.Apply(cmd, new LinuxPlaybackCommandHost(this));
    }

    private sealed class LinuxPlaybackCommandHost(LinuxVideoPlayerService player) : IPlaybackCommandHost
    {
        public void BeginIndexSwitchSuppression() => player._suppressIndexDrivenSwitch = true;

        public void EndIndexSwitchSuppression() => player._suppressIndexDrivenSwitch = false;

        public void ClearCurrentResolvedUrl() => player._pool.ClearCurrentResolvedUrl();

        public void RemoveCurrentFromPool() => player._pool.RemoveCurrentFromPool();

        public void SyncHealthyStreamCount() => player._pool.SyncHealthyStreamCount();

        public void ReportFailed(string? reason)
        {
            player._logger.LogWarning("[LinuxVideoPlayerService] Stream failed: {Reason}", reason);
            _ = player._healthReporter.ReportPlaybackErrorAsync(
                player._session.Snapshot.CurrentUrl,
                player._refererUrl,
                player.CurrentHealthStreamName(),
                error: reason);
        }

        public void ReportDeclined(string? reason)
        {
            player._logger.LogWarning("[LinuxVideoPlayerService] Stream declined: {Reason}", reason);
            _ = player._healthReporter.ReportPlaybackErrorAsync(
                player._session.Snapshot.CurrentUrl,
                player._refererUrl,
                player.CurrentHealthStreamName(),
                error: reason);
        }

        public void ReportWorking()
        {
            player._logger.LogInformation("[LinuxVideoPlayerService] Stream established");
            _ = player._healthReporter.ReportPlaybackStartedAsync(
                player._session.Snapshot.CurrentUrl,
                player._refererUrl,
                player.CurrentHealthStreamName(),
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
                    player.CurrentHealthStreamName(),
                    metrics: player.GetCurrentMetrics());
            }
        }

        public void Attach(string url, bool isRevert)
        {
            if (isRevert)
                player._logger.LogWarning("[LinuxVideoPlayerService] Reverting to last good stream: {Url}", url);
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

        public void SwitchPoolToPrevious() => player._pool.SwitchPoolToPrevious();

        public void NotifyApplyFailed(Exception exception)
            => player._logger.LogWarning(exception, "[LinuxVideoPlayerService] ApplyPlaybackCommand failed");
    }

    private async Task AttachLibVlcAsync(
        string m3u8Url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken)
    {
        var generation = _session.Snapshot.AttachGeneration;

        // Yield SoundFlow before LibVLC session create/Play so Pulse is free
        // when --aout=pulse opens (PlaybackAudioSession + probe docs).
        PlaybackVisibilityChanged?.Invoke(this, true);

        // Pulse/PipeWire can still hold the sink briefly after miniaudio
        // Dispose. Settle here (async, attach path) — never inside
        // IUiSoundPlayer.YieldDevice, which visibility handlers may call on UI.
        await Task.Delay(UiSoundHandoffSettle, cancellationToken);

        var vlc = await EnsureSessionAsync();
        if (vlc == null)
        {
            PlaybackVisibilityChanged?.Invoke(this, false);
            _engine.Raise(MediaEngineEvent.Error(generation, "LibVLC is not initialized"));
            return;
        }

#if EMBEDDED_LINUX_VIDEO
        // Show the in-app surface first, attach software-frame callbacks, then
        // play — callbacks must be set before the vout is created or libvlc
        // opens its own window.
        await TryEmbedSurfaceAsync(vlc);
#endif

        var current = _switching.GetCurrentStream();
        if (current?.RequestHeaders is { Count: > 0 })
            requestHeaders = current.RequestHeaders;
        if (!string.IsNullOrWhiteSpace(current?.Referer))
            _refererUrl = current.Referer;

        var referer = ResolveHeader(requestHeaders, "referer") ?? _refererUrl;
        var userAgent = ResolveHeader(requestHeaders, "user-agent")
            ?? "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        var origin = ResolveHeader(requestHeaders, "origin");

        // LibVLC's C HTTP stack cannot use DualStack/DoH and prefers AAAA on
        // WSL (Network is unreachable). Play through a loopback proxy so .NET
        // fetches playlists/segments and VLC only opens 127.0.0.1.
        var playUrl = m3u8Url;
        var playViaProxy = false;
        if (_hlsProxy is not null && _hlsProxy.TryStart())
        {
            try
            {
                _hlsProxy.SetPlaybackHeaders(requestHeaders, referer);
                _hlsProxy.SetRewrittenSegments(_switching.GetCurrentStream()?.RewrittenSegments);
                playUrl = _hlsProxy.Wrap(m3u8Url).AbsoluteUri;
                playViaProxy = true;
                _logger.LogInformation(
                    "[LinuxVideoPlayerService] HLS via loopback proxy {ProxyUrl} (origin {Origin})",
                    playUrl,
                    m3u8Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LinuxVideoPlayerService] HLS proxy wrap failed; falling back to direct CDN URL");
            }
        }

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
            foreach (var option in LinuxPlatformProbe.BuildPlaybackMediaOptions(
                         UseConservativeVlcOptions,
                         playViaProxy ? null : referer,
                         userAgent,
                         playViaProxy ? null : origin))
            {
                media.AddOption(option);
            }

            ConfigureAudioOutput(vlc.Player);
            _currentMedia = media;
            vlc.Player.Play(media);
        });

        if (!attached)
        {
            _engine.Raise(MediaEngineEvent.Error(generation, "libvlc attach did not complete (instance abandoned)"));
            return;
        }

        _logger.LogInformation("[LinuxVideoPlayerService] Requested LibVLC attach for {Url}", m3u8Url);
        _ = WatchAttachProgressAsync(generation, m3u8Url, cancellationToken);
    }

    /// <summary>
    /// If Playing never fires, Video Info still says "Playing" (chrome label)
    /// with pending metrics. Probe LibVLC state and fail the attempt so the
    /// session can advance instead of sitting on a black screen.
    /// </summary>
    private async Task WatchAttachProgressAsync(
        long generation,
        string m3u8Url,
        CancellationToken cancellationToken)
    {
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                if (_session.Snapshot.AttachGeneration != generation)
                    return;

                var playing = _current is { Abandoned: false } session && session.Player.IsPlaying;
                if (!playing)
                    continue;

                if (_session.Snapshot.IsPreparing)
                {
                    _logger.LogInformation(
                        "[LinuxVideoPlayerService] LibVLC is playing without a Playing event; treating as Ready ({Url})",
                        m3u8Url);
                    RaiseReadyFromCurrentPlayer();
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_session.Snapshot.AttachGeneration != generation)
        {
            return;
        }

        var vlc = _current;
        if (vlc is not { Abandoned: false })
        {
            return;
        }

        try
        {
            if (vlc.Player.IsPlaying)
            {
                if (_session.Snapshot.IsPreparing)
                    RaiseReadyFromCurrentPlayer();
                return;
            }

            var mediaState = vlc.Player.Media?.State.ToString() ?? "(no media)";
            _logger.LogWarning(
                "[LinuxVideoPlayerService] No Playing event after attach; player.IsPlaying={IsPlaying}, media.State={MediaState}, url={Url}",
                vlc.Player.IsPlaying,
                mediaState,
                m3u8Url);
            _engine.Raise(MediaEngineEvent.Error(
                generation,
                $"LibVLC did not start playback (media state {mediaState})"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxVideoPlayerService] Attach watchdog failed");
        }
    }

    private void RaiseReadyFromCurrentPlayer()
    {
        if (_current is { Abandoned: false } vlc)
            EnsurePlaybackAudio(vlc.Player);

        PlaybackVisibilityChanged?.Invoke(this, true);
        SetBufferingState(false);
        _engine.Raise(MediaEngineEvent.Ready(_session.Snapshot.AttachGeneration));
        StartMetricsLoop();
    }

    private static string? ResolveHeader(IReadOnlyDictionary<string, string>? headers, string name)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        foreach (var pair in headers)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
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

        _logger.LogInformation("[LinuxVideoPlayerService] Playback started");
        _bufferCachePercent = 100f;
        RaiseReadyFromCurrentPlayer();
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        if (!IsCurrentPlayer(sender))
        {
            return;
        }

        var isBuffering = e.Cache < 100f;
        _bufferCachePercent = e.Cache;
        _logger.LogDebug("[LinuxVideoPlayerService] Buffering: {Percentage}%", e.Cache);
        SetBufferingState(isBuffering);
        _engine.Raise(MediaEngineEvent.Buffering(_session.Snapshot.AttachGeneration, isBuffering));
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        if (!IsCurrentPlayer(sender))
        {
            return;
        }

        _logger.LogError("[LinuxVideoPlayerService] Playback error encountered");
        StopMetricsLoop();
        _engine.Raise(MediaEngineEvent.Error(_session.Snapshot.AttachGeneration, "Stream playback failed"));
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (!IsCurrentPlayer(sender))
        {
            return;
        }

        _logger.LogInformation("[LinuxVideoPlayerService] Playback ended");
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

    private string? CurrentHealthStreamName()
    {
        var stream = _switching.GetCurrentStream()?.Stream;
        return stream == null ? null : StreamHealthIdentity.GetStreamName(stream);
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
            _logger.LogWarning(ex, "[LinuxVideoPlayerService] Error getting playback metrics");
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
                _logger.LogDebug(ex, "[LinuxVideoPlayerService] Metrics raise failed");
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
        _logger.LogInformation("[LinuxVideoPlayerService] Disposing");
        _indexSubscription.Dispose();
        StopMetricsLoop();
        _hlsProxy?.Dispose();

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
                vlc.FrameSink?.Dispose();
                vlc.FrameSink = null;
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
                _logger.LogWarning(ex, "[LinuxVideoPlayerService] Error during disposal");
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
                    _logger.LogError("[LinuxVideoPlayerService] {Message}", renderedMessage);
                    if (LibVlcPlaybackFailure.IsFatalAdaptiveDemux(module, message))
                    {
                        RaisePlaybackFailureOnce("LibVLC adaptive demux failed (segment not playable)");
                    }
                    else if (LibVlcPlaybackFailure.IsHttpForbidden(module, message))
                    {
                        RaisePlaybackFailureOnce("LibVLC HTTP 403 (CDN rejected request headers)");
                    }

                    return;
                }

                if (IsLibVlcWarningLevel(level))
                {
                    _logger.LogWarning("[LinuxVideoPlayerService] {Message}", renderedMessage);
                    return;
                }

                if (IsRenderDiagnosticInteresting(message))
                {
                    _logger.LogInformation("[LinuxVideoPlayerService] {Message}", renderedMessage);
                }
            }
            catch
            {
            }
        };

        vlc.LibVlc.Log += vlc.LogHandler;
        _logger.LogInformation("[LinuxVideoPlayerService] LibVLC native diagnostics enabled");
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

    private void RaisePlaybackFailureOnce(string reason)
    {
        var generation = _session.Snapshot.AttachGeneration;
        if (Interlocked.Exchange(ref _demuxFailureGeneration, generation) == generation)
        {
            return;
        }

        _logger.LogWarning("[LinuxVideoPlayerService] Treating LibVLC failure as stream error: {Reason}", reason);
        _engine.Raise(MediaEngineEvent.Error(generation, reason));
    }
}
