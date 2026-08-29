namespace VardyParty.Ports;

/// <summary>
/// Persists the "UI sounds" and "Goal notifications" toggles. Mirrors
/// ILeagueFilterPreferencesStore: heads plug in Preferences (MAUI) or a file
/// store; tests use in-memory.
/// </summary>
public interface ISoundPreferencesStore
{
    bool HasSavedPreferences { get; }

    /// <summary>Returns the saved value, or TRUE when nothing was saved (default on).</summary>
    bool LoadUiSoundsEnabled();

    void SaveUiSoundsEnabled(bool enabled);

    /// <summary>Returns the saved value, or TRUE when nothing was saved (default on).</summary>
    bool LoadGoalNotificationsEnabled();

    void SaveGoalNotificationsEnabled(bool enabled);

    void ClearSavedPreferences();
}
