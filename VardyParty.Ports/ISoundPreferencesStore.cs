namespace VardyParty.Ports;

/// <summary>
/// Persists the "UI sounds" toggle. Mirrors ILeagueFilterPreferencesStore:
/// heads plug in Preferences (MAUI) or a file store; tests use in-memory.
/// </summary>
public interface ISoundPreferencesStore
{
    bool HasSavedPreferences { get; }

    /// <summary>Returns the saved value, or TRUE when nothing was saved (default on).</summary>
    bool LoadUiSoundsEnabled();

    void SaveUiSoundsEnabled(bool enabled);

    void ClearSavedPreferences();
}
