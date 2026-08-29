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
///
/// Threading: Windows.Media.Playback.MediaPlayer is a WinRT agile class
/// (metadata MarshalingBehavior=Agile, ThreadingModel=Both — see the class
/// page on learn.microsoft.com), so creating and configuring it on the
/// background init task is legal and needs no DispatcherQueue. The two things
/// that CAN surface as 0xc000027b stowed exceptions are handled explicitly:
/// async media failures are observed via <see cref="MediaPlayer.MediaFailed"/>
/// (a logging handler that never throws back into the WinRT callback), and
/// System Media Transport Controls integration is disabled
/// (CommandManager.IsEnabled=false) so six sound-effect players never touch
/// the SMTC/CoreMessaging machinery during startup.
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
    private int _mediaFailedLogged;

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
                if (!File.Exists(localPath) || new FileInfo(localPath).Length == 0)
                {
                    await using var source = await OpenSoundStreamAsync(asset);
                    await using var target = File.Create(localPath);
                    await source.CopyToAsync(target, cancellationToken);
                }

                var player = new MediaPlayer
                {
                    AudioCategory = MediaPlayerAudioCategory.SoundEffects,
                    // MediaPlayer (unlike MediaElement) does not auto-play by
                    // default; explicit so preloading can never start playback.
                    AutoPlay = false,
                };

                // Sound effects must not appear in (or wire up) the System
                // Media Transport Controls.
                player.CommandManager.IsEnabled = false;

                // Observe async failures BEFORE attaching the source: an
                // unobserved async WinRT failure is exactly what surfaces as a
                // 0xc000027b stowed-exception crash.
                var failedSound = sound;
                player.MediaFailed += (_, args) => OnMediaFailed(failedSound, args);

                player.Source = MediaSource.CreateFromUri(new Uri(localPath));
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

    private static async Task<Stream> OpenSoundStreamAsync(string asset)
    {
        var names = new[]
        {
            asset,
            asset.Replace('/', '\\'),
            Path.GetFileName(asset),
            Path.Combine("Sounds", Path.GetFileName(asset)),
        };

        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return await FileSystem.OpenAppPackageFileAsync(name);
            }
            catch (FileNotFoundException)
            {
            }
            catch (IOException)
            {
            }
        }

        foreach (var dir in new[] { AppContext.BaseDirectory, FileSystem.AppDataDirectory })
        {
            var disk = Path.Combine(dir, "Sounds", Path.GetFileName(asset));
            if (File.Exists(disk))
            {
                return File.OpenRead(disk);
            }
        }

        throw new FileNotFoundException($"UI sound asset not found: {asset}");
    }

    private void OnMediaFailed(UiSound sound, MediaPlayerFailedEventArgs args)
    {
        try
        {
            if (Interlocked.Exchange(ref _mediaFailedLogged, 1) == 0)
            {
                _logger.LogWarning(
                    args.ExtendedErrorCode,
                    "UI sound {Sound} failed asynchronously ({Error}: {Message}); further failures muted",
                    sound, args.Error, args.ErrorMessage);
            }
        }
        catch
        {
            // Never throw back into the WinRT callback — that would itself
            // become a stowed exception.
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
