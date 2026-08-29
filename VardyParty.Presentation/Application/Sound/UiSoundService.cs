using VardyParty.Ports;

namespace VardyParty.Presentation;

/// <summary>
/// Shared UI-sound policy in front of the platform <see cref="IUiSoundPlayer"/>:
/// honours the persisted "UI sounds" toggle (default ON), suppresses everything
/// while stream playback is visible (<see cref="SuppressAll"/> — blips must
/// never play over commentary), and throttles focus ticks to one per 40ms so
/// D-pad autorepeat doesn't machine-gun.
/// </summary>
public sealed class UiSoundService
{
    /// <summary>Minimum gap between two focus ticks.</summary>
    public static readonly TimeSpan FocusThrottle = TimeSpan.FromMilliseconds(40);

    private readonly IUiSoundPlayer _player;
    private readonly ISoundPreferencesStore _preferences;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private bool? _enabledCache;
    private long _lastFocusTimestamp;
    private bool _focusPlayedOnce;

    public UiSoundService(IUiSoundPlayer player, ISoundPreferencesStore preferences, TimeProvider? time = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True while stream playback is visible; every sound is muted.</summary>
    public bool SuppressAll { get; set; }

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabledCache ??= _preferences.LoadUiSoundsEnabled();
            }
        }
    }

    /// <summary>Persists the toggle. Turning ON plays the Select sound as confirmation.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabledCache = enabled;
        }

        _preferences.SaveUiSoundsEnabled(enabled);

        if (enabled && !SuppressAll)
        {
            _player.Play(UiSound.Select);
        }
    }

    /// <summary>Fire-and-forget; applies the toggle, suppression, and focus throttle.</summary>
    public void Play(UiSound sound)
    {
        if (SuppressAll || !Enabled)
        {
            return;
        }

        if (sound == UiSound.FocusMove)
        {
            lock (_gate)
            {
                var now = _time.GetTimestamp();
                if (_focusPlayedOnce && _time.GetElapsedTime(_lastFocusTimestamp, now) < FocusThrottle)
                {
                    return;
                }

                _lastFocusTimestamp = now;
                _focusPlayedOnce = true;
            }
        }

        _player.Play(sound);
    }
}
