#if ANDROID
using Android.Media;
using Microsoft.Extensions.Logging;
using VardyParty.Ports;

namespace VardyParty.Platforms.Android;

/// <summary>
/// SoundPool-backed player: all six WAVs are loaded from the APK assets in
/// <see cref="InitializeAsync"/> (awaiting SoundPool's LoadComplete), so
/// <see cref="Play"/> is a non-blocking native trigger. AudioAttributes use
/// AssistanceSonification and deliberately request NO audio focus — a 30ms
/// tick must never pause the user's podcast in another app.
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

    private readonly ILogger<AndroidUiSoundPlayer> _logger;
    private readonly Dictionary<UiSound, int> _soundIds = new();
    private SoundPool? _pool;
    private volatile bool _ready;
    private int _playFailureLogged;

    public AndroidUiSoundPlayer(ILogger<AndroidUiSoundPlayer> logger) => _logger = logger;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.AssistanceSonification)!
                .SetContentType(AudioContentType.Sonification)!
                .Build()!;

            var pool = new SoundPool.Builder()
                .SetMaxStreams(3)!
                .SetAudioAttributes(attributes)!
                .Build()!;

            var pending = new Dictionary<int, TaskCompletionSource<bool>>();
            pool.LoadComplete += (_, e) =>
            {
                if (pending.TryGetValue(e.SampleId, out var tcs))
                {
                    tcs.TrySetResult(e.Status == 0);
                }
            };

            var assets = global::Android.App.Application.Context.Assets
                ?? throw new InvalidOperationException("No asset manager");

            foreach (var (sound, asset) in Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var descriptor = assets.OpenFd(asset);
                var id = pool.Load(descriptor, priority: 1);
                pending[id] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _soundIds[sound] = id;
            }

            // SoundPool decodes asynchronously; Play() before LoadComplete is a no-op.
            await Task.WhenAll(pending.Values.Select(t => t.Task)).WaitAsync(
                TimeSpan.FromSeconds(10), cancellationToken);

            _pool = pool;
            _ready = true;
            _logger.LogInformation("UI sounds initialised ({Count} sounds)", _soundIds.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UI sound init failed; sounds disabled");
        }
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
        _ready = false;
        try
        {
            _pool?.Release();
            _pool?.Dispose();
        }
        catch
        {
        }

        _pool = null;
    }
}
#endif
