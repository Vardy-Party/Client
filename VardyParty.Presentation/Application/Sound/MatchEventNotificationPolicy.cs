using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Presentation;

/// <summary>
/// Decides how a detected match event (goal / extra time / penalties) is
/// delivered. The user-decided table (see the notification matrix in
/// docs/architecture/homepage-maui-avalonia.md):
///
/// - App foregrounded AND homepage is the active surface (no stream playing):
///   sting + toast + card flash.
/// - Stream playing (native player open): toast + flash for OTHER games,
///   never for the fixture whose stream is on screen; NO audio.
/// - App minimized/background: nothing at all — events are dropped, never
///   queued for a catch-up on resume.
/// - The "Goal notifications" toggle (default ON, persisted like the UI
///   sounds toggle) gates everything; the separate "UI sounds" toggle still
///   gates the sting's AUDIO on top (enforced by UiSoundService, which the
///   audio is routed through).
///
/// Heads own the inputs: window lifecycle events set
/// <see cref="IsAppForegrounded"/> (Activated/Resumed = foregrounded,
/// Stopped = background; Deactivated — focus lost while still visible, e.g.
/// the desktop head's native VLC window taking focus — deliberately does NOT
/// count as background, or the "playing → toast only" row could never
/// deliver on desktop), and the existing playback-visibility wiring sets
/// <see cref="IsPlaybackActive"/>. <c>SelectionState.CurrentGame</c> is the
/// watched fixture while a stream is up.
/// </summary>
public sealed class MatchEventNotificationPolicy
{
    private readonly ISoundPreferencesStore _preferences;
    private readonly object _gate = new();
    private bool? _enabledCache;

    public MatchEventNotificationPolicy(ISoundPreferencesStore preferences) =>
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

    /// <summary>App window is visible (not stopped/minimized). Heads keep this current.</summary>
    public bool IsAppForegrounded { get; set; } = true;

    /// <summary>The native stream player is on screen (same signal that suppresses UI blips).</summary>
    public bool IsPlaybackActive { get; set; }

    /// <summary>Settings: the persisted "Goal notifications" switch (default ON).</summary>
    public bool NotificationsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabledCache ??= _preferences.LoadGoalNotificationsEnabled();
            }
        }
    }

    /// <summary>Persists the toggle.</summary>
    public void SetNotificationsEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabledCache = enabled;
        }

        _preferences.SaveGoalNotificationsEnabled(enabled);
    }

    /// <summary>
    /// Toast + card flash may show. False drops the event entirely (no
    /// catch-up on resume).
    /// </summary>
    public bool ShouldPresent => NotificationsEnabled && IsAppForegrounded;

    /// <summary>
    /// The sting may play (route it through UiSoundService, which adds the
    /// "UI sounds" toggle and playback suppression on top).
    /// </summary>
    public bool ShouldPlayAudio => ShouldPresent && !IsPlaybackActive;

    /// <summary>
    /// Per-event gate on top of <see cref="ShouldPresent"/>: while a stream
    /// is playing, the watched fixture is silent (the viewer can see the
    /// goal). Other live games still toast.
    /// </summary>
    public bool ShouldPresentEvent(MatchEvent matchEvent, Game? watchingGame)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);

        if (!ShouldPresent)
            return false;

        return !(IsPlaybackActive && HomePlaybackIntent.SameGame(watchingGame, matchEvent.Game));
    }
}

