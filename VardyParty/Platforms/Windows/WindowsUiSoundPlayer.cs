#if WINDOWS
using Microsoft.Extensions.Logging;
using VardyParty.Ports;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace VardyParty.Platforms.Windows;

/// <summary>
/// One preloaded <see cref="MediaPlayer"/> per sound with
/// AudioCategory=SoundEffects, so overlapping blips mix (System.Media.SoundPlayer
/// cannot). WAVs ship as MauiAssets; they are copied once to local app data at
/// initialize because MediaPlayer needs a URI and OpenAppPackageFileAsync works
/// in both packaged (MSIX) and unpackaged runs.
/// </summary>
public sealed class WindowsUiSoundPlayer : IUiSoundPlayer, IDisposable
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

    private readonly ILogger<WindowsUiSoundPlayer> _logger;
    private readonly Dictionary<UiSound, MediaPlayer> _players = new();
    private volatile bool _ready;
    private int _playFailureLogged;

    public WindowsUiSoundPlayer(ILogger<WindowsUiSoundPlayer> logger) => _logger = logger;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cacheDir = Path.Combine(FileSystem.CacheDirectory, "UiSounds");
            Directory.CreateDirectory(cacheDir);

            foreach (var (sound, asset) in Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var localPath = Path.Combine(cacheDir, Path.GetFileName(asset));
                if (!File.Exists(localPath))
                {
                    using var source = await FileSystem.OpenAppPackageFileAsync(asset);
                    using var target = File.Create(localPath);
                    await source.CopyToAsync(target, cancellationToken);
                }

                var player = new MediaPlayer
                {
                    AudioCategory = MediaPlayerAudioCategory.SoundEffects,
                    Source = MediaSource.CreateFromUri(new Uri(localPath)),
                };
                _players[sound] = player;
            }

            _ready = true;
            _logger.LogInformation("UI sounds initialised ({Count} sounds)", _players.Count);
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
        if (!_ready || !_players.TryGetValue(sound, out var player))
        {
            return;
        }

        try
        {
            player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
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
        foreach (var player in _players.Values)
        {
            try
            {
                player.Dispose();
            }
            catch
            {
            }
        }

        _players.Clear();
    }
}
#endif
