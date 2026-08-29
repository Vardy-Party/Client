using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Providers;
using SoundFlow.Structs;
using VardyParty.Ports;

namespace VardyParty.Desktop.Services;

/// <summary>
/// SoundFlow (miniaudio) implementation for the desktop head: one engine and
/// playback device, each WAV decoded once at initialize into an in-memory
/// provider with a persistent SoundPlayer on the master mixer. Play() rewinds
/// and retriggers — no allocation, no I/O. Every native call is guarded: a
/// headless machine or vanished audio device must degrade to silence, never
/// crash (the log-once flag keeps that quiet).
///
/// Device sharing: libvlc and miniaudio both talk to Pulse/ALSA. Holding the
/// miniaudio device across a stream session leaves video silent and poisons
/// this engine (Play then fails forever). <see cref="YieldDevice"/> tears the
/// native device down before Play; <see cref="RecoverDevice"/> rebuilds it
/// after Close. See <see cref="VardyParty.Presentation.PlaybackAudioSession"/>.
/// </summary>
public sealed class SoundFlowUiSoundPlayer : IUiSoundPlayer, IDisposable
{
    private static readonly (UiSound Sound, string File)[] Files =
    [
        (UiSound.FocusMove, "focus_tick.wav"),
        (UiSound.Select, "select.wav"),
        (UiSound.Back, "back.wav"),
        (UiSound.MenuOpen, "menu_open.wav"),
        (UiSound.Error, "error.wav"),
        (UiSound.Goal, "goal.wav"),
    ];

    private readonly ILogger<SoundFlowUiSoundPlayer> _logger;
    private readonly Dictionary<UiSound, SoundPlayer> _players = new();
    private readonly object _gate = new();
    private MiniAudioEngine? _engine;
    private AudioPlaybackDevice? _device;
    private volatile bool _ready;
    private bool _yielded;
    private int _epoch;
    private int _playFailureLogged;

    public SoundFlowUiSoundPlayer(ILogger<SoundFlowUiSoundPlayer> logger) => _logger = logger;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => BringUp(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public void YieldDevice()
    {
        lock (_gate)
        {
            _yielded = true;
            _epoch++;
            TearDownUnlocked();
        }

        _logger.LogInformation("UI sound device yielded for video playback");
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
                BringUp(CancellationToken.None);
                _logger.LogInformation("UI sound device recovered after video playback");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UI sound device recovery failed; sounds stay silent");
            }
        });
    }

    public void Play(UiSound sound)
    {
        if (!_ready || !_players.TryGetValue(sound, out var player))
        {
            return;
        }

        try
        {
            player.Stop(); // rewinds to the start
            player.Play();
        }
        catch (Exception ex)
        {
            _ready = false;
            if (Interlocked.Exchange(ref _playFailureLogged, 1) == 0)
            {
                _logger.LogWarning(ex, "UI sound playback failed (audio device lost?); further failures muted until recover");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _yielded = true;
            _epoch++;
            TearDownUnlocked();
        }
    }

    private void BringUp(CancellationToken cancellationToken)
    {
        int epoch;
        lock (_gate)
        {
            if (_ready)
            {
                return;
            }

            epoch = _epoch;
        }

        try
        {
            var soundsDir = Path.Combine(AppContext.BaseDirectory, "Sounds");
            if (!Directory.Exists(soundsDir))
            {
                _logger.LogWarning("UI sounds directory missing at {Dir}; sounds disabled", soundsDir);
                return;
            }

            var engine = new MiniAudioEngine();
            var device = engine.InitializePlaybackDevice(null, AudioFormat.Dvd); // 48 kHz
            var loaded = new Dictionary<UiSound, SoundPlayer>();
            foreach (var (sound, file) in Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(soundsDir, file);
                if (!File.Exists(path))
                {
                    _logger.LogWarning("UI sound file missing: {Path}", path);
                    continue;
                }

                // Decoded once into memory; the player stays on the mixer.
                var provider = new AssetDataProvider(engine, File.ReadAllBytes(path));
                var player = new SoundPlayer(engine, device.Format, provider);
                device.MasterMixer.AddComponent(player);
                loaded[sound] = player;
            }

            device.Start();

            lock (_gate)
            {
                if (_epoch != epoch || _yielded)
                {
                    // A yield/recover raced us — drop this generation.
                    StopAndDispose(device, engine);
                    return;
                }

                TearDownUnlocked();
                foreach (var pair in loaded)
                {
                    _players[pair.Key] = pair.Value;
                }

                _engine = engine;
                _device = device;
                _ready = _players.Count > 0;
                _playFailureLogged = 0;
            }

            _logger.LogInformation("UI sounds initialised ({Count} sounds)", loaded.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // No audio device (headless CI, unplugged sink) — stay silent.
            _logger.LogWarning(ex, "UI sound engine unavailable; sounds disabled");
        }
    }

    private void TearDownUnlocked()
    {
        _ready = false;
        foreach (var player in _players.Values)
        {
            try
            {
                player.Stop();
            }
            catch
            {
            }
        }

        _players.Clear();
        StopAndDispose(_device, _engine);
        _device = null;
        _engine = null;
    }

    private static void StopAndDispose(AudioPlaybackDevice? device, MiniAudioEngine? engine)
    {
        try
        {
            device?.Stop();
        }
        catch
        {
        }

        try
        {
            device?.Dispose();
        }
        catch
        {
        }

        try
        {
            engine?.Dispose();
        }
        catch
        {
        }
    }
}
