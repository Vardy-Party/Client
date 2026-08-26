using Microsoft.Extensions.Logging;
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
    private MiniAudioEngine? _engine;
    private IDisposable? _device;
    private volatile bool _ready;
    private int _playFailureLogged;

    public SoundFlowUiSoundPlayer(ILogger<SoundFlowUiSoundPlayer> logger) => _logger = logger;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
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
                    _players[sound] = player;
                }

                device.Start();
                _engine = engine;
                _device = device;
                _ready = _players.Count > 0;
                _logger.LogInformation("UI sounds initialised ({Count} sounds)", _players.Count);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // No audio device (headless CI, unplugged sink) — stay silent.
                _logger.LogWarning(ex, "UI sound engine unavailable; sounds disabled");
            }
        }, cancellationToken);
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
            if (Interlocked.Exchange(ref _playFailureLogged, 1) == 0)
            {
                _logger.LogWarning(ex, "UI sound playback failed (audio device lost?); further failures muted");
            }
        }
    }

    public void Dispose()
    {
        _ready = false;
        try
        {
            _device?.Dispose();
            _engine?.Dispose();
        }
        catch
        {
        }
    }
}
