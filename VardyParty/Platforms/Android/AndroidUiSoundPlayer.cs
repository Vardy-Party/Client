#if ANDROID
using Android.Content.Res;
using Android.Media;
using Microsoft.Extensions.Logging;
using VardyParty.Ports;

namespace VardyParty.Platforms.Android;

/// <summary>
/// SoundPool-backed player: WAVs load from APK assets in
/// <see cref="InitializeAsync"/> (awaiting SoundPool's LoadComplete), so
/// <see cref="Play"/> is a non-blocking native trigger. Phones/tablets use
/// AssistanceSonification with no audio focus so a 30ms tick never ducks
/// another app's podcast. Android TV uses USAGE_MEDIA — sonification pools
/// stall LoadComplete for 10s+ on BRAVIA ATV3 (field: silent UI + leaked
/// pool → FinalizerWatchdog crash on stream start).
///
/// ExoPlayer in NativeVideoActivity takes the mixer. A live SoundPool then
/// stays silent after the activity finishes until it is released and
/// reloaded — field: ticks dead until Settings → UI sounds was toggled.
/// <see cref="YieldDevice"/> releases the pool before playback;
/// <see cref="RecoverDevice"/> rebuilds it when the homepage reappears.
///
/// Critical: every SoundPool we construct must be Release()'d on this
/// thread. Leaving one for GC after a LoadComplete timeout lets
/// FinalizerWatchdogDaemon kill the process when native_release blocks
/// behind ExoPlayer (field: crash on stream start on 32-bit TV).
///
/// TV decode is slow and flaky under cold-start load: AssetFileDescriptors
/// stay open until LoadComplete, samples load one-at-a-time, and a partial
/// set is adopted rather than abandoning all sounds on timeout.
/// </summary>
public sealed class AndroidUiSoundPlayer : IUiSoundPlayer, IDisposable
{
    private static readonly (UiSound Sound, string Asset)[] Assets =
    [
        (UiSound.FocusMove, "Sounds/focus_tick.wav"),
        (UiSound.Select, "Sounds/select.wav"),
        (UiSound.Back, "Sounds/back.wav"),
        (UiSound.MenuOpen, "Sounds/menu_open.wav"),
        (UiSound.Error, "Sounds/error.wav"),
        (UiSound.Goal, "Sounds/goal.wav"),
    ];

    /// <summary>Per-sample LoadComplete budget on a saturated 32-bit TV.</summary>
    private static readonly TimeSpan PerSampleBudget = TimeSpan.FromSeconds(20);

    private readonly ILogger<AndroidUiSoundPlayer> _logger;
    private readonly Dictionary<UiSound, int> _soundIds = new();
    private readonly object _gate = new();
    private SoundPool? _pool;
    private volatile bool _ready;
    private volatile bool _yielded;
    private int _epoch;
    private int _playFailureLogged;

    public AndroidUiSoundPlayer(ILogger<AndroidUiSoundPlayer> logger) => _logger = logger;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        BringUpAsync(cancellationToken);

    /// <inheritdoc />
    public void YieldDevice()
    {
        lock (_gate)
        {
            _yielded = true;
            _epoch++;
            TearDownUnlocked();
        }

        _logger.LogInformation("UI sound pool yielded for video playback");
    }

    /// <inheritdoc />
    public void RecoverDevice()
    {
        lock (_gate)
        {
            _yielded = false;
            _epoch++;
            TearDownUnlocked();
        }

        _ = Task.Run(() =>
        {
            try
            {
                BringUpAsync(CancellationToken.None).GetAwaiter().GetResult();
                _logger.LogInformation("UI sound pool recovered after video playback");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UI sound pool recovery failed; sounds stay silent");
            }
        });
    }

    public void Play(UiSound sound)
    {
        var pool = _pool;
        if (!_ready || pool == null || !_soundIds.TryGetValue(sound, out var id))
        {
            return;
        }

        try
        {
            pool.Play(id, leftVolume: 1f, rightVolume: 1f, priority: 0, loop: 0, rate: 1f);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _playFailureLogged, 1) == 0)
            {
                _logger.LogWarning(ex, "UI sound playback failed; further failures muted");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _epoch++;
            _yielded = true;
            TearDownUnlocked();
        }
    }

    private async Task BringUpAsync(CancellationToken cancellationToken)
    {
        int epoch;
        lock (_gate)
        {
            if (_yielded || _ready)
            {
                return;
            }

            epoch = _epoch;
        }

        SoundPool? pool = null;
        EventHandler<SoundPool.LoadCompleteEventArgs>? loadHandler = null;
        var openDescriptors = new List<AssetFileDescriptor>();
        try
        {
            // TV firmwares often stall ASSISTANCE_SONIFICATION SoundPool loads.
            // USAGE_MEDIA loads reliably on Android TV; phones keep sonification
            // so UI ticks stay out of the media ducking path.
            var onTelevision = MauiProgram.IsTv;
            var attributes = new AudioAttributes.Builder()
                .SetUsage(onTelevision ? AudioUsageKind.Media : AudioUsageKind.AssistanceSonification)!
                .SetContentType(onTelevision ? AudioContentType.Music : AudioContentType.Sonification)!
                .Build()!;

            pool = new SoundPool.Builder()
                .SetMaxStreams(3)!
                .SetAudioAttributes(attributes)!
                .Build()!;

            var pending = new Dictionary<int, TaskCompletionSource<bool>>();
            loadHandler = (_, e) =>
            {
                if (pending.TryGetValue(e.SampleId, out var tcs))
                {
                    tcs.TrySetResult(e.Status == 0);
                }
            };
            pool.LoadComplete += loadHandler;

            var assets = global::Android.App.Application.Context.Assets
                ?? throw new InvalidOperationException("No asset manager");

            var loaded = new Dictionary<UiSound, int>();

            // One sample at a time, AFD held open until LoadComplete — parallel
            // Load() + disposing OpenFd early left samples undecoded on TV
            // (10s WhenAll timeout → silent UI + leaked pool → crash on yield).
            foreach (var (sound, asset) in Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_yielded || epoch != _epoch)
                    {
                        return;
                    }
                }

                var descriptor = assets.OpenFd(asset);
                openDescriptors.Add(descriptor);
                var id = pool.Load(descriptor, priority: 1);
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[id] = tcs;

                bool ok;
                try
                {
                    ok = await tcs.Task.WaitAsync(PerSampleBudget, cancellationToken);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "UI sound load timed out for {Sound} after {Budget}s — keeping samples loaded so far",
                        sound, PerSampleBudget.TotalSeconds);
                    break;
                }

                if (ok)
                {
                    loaded[sound] = id;
                }
                else
                {
                    _logger.LogWarning("UI sound LoadComplete status failed for {Sound}", sound);
                }
            }

            lock (_gate)
            {
                if (_yielded || epoch != _epoch || loaded.Count == 0)
                {
                    ReleasePool(pool, loadHandler);
                    pool = null;
                    return;
                }

                _soundIds.Clear();
                foreach (var pair in loaded)
                {
                    _soundIds[pair.Key] = pair.Value;
                }

                _pool = pool;
                if (loadHandler != null)
                {
                    try
                    {
                        pool.LoadComplete -= loadHandler;
                    }
                    catch
                    {
                    }
                }

                pool = null;
                loadHandler = null;
                _ready = true;
                _playFailureLogged = 0;
            }

            _logger.LogInformation(
                "UI sounds initialised ({Count}/{Total} sounds, tv={IsTv})",
                loaded.Count, Assets.Length, MauiProgram.IsTv);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UI sound init failed; sounds disabled");
        }
        finally
        {
            foreach (var descriptor in openDescriptors)
            {
                try
                {
                    descriptor.Close();
                }
                catch
                {
                }

                try
                {
                    descriptor.Dispose();
                }
                catch
                {
                }
            }

            if (pool != null)
            {
                ReleasePool(pool, loadHandler);
            }
        }
    }

    private void TearDownUnlocked()
    {
        _ready = false;
        var pool = _pool;
        _pool = null;
        _soundIds.Clear();
        if (pool != null)
        {
            ReleasePool(pool, loadHandler: null);
        }
    }

    private static void ReleasePool(SoundPool pool, EventHandler<SoundPool.LoadCompleteEventArgs>? loadHandler)
    {
        try
        {
            if (loadHandler != null)
            {
                pool.LoadComplete -= loadHandler;
            }
        }
        catch
        {
        }

        try
        {
            pool.Release();
        }
        catch
        {
        }

        try
        {
            pool.Dispose();
        }
        catch
        {
        }
    }
}
#endif
