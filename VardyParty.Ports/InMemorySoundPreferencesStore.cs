namespace VardyParty.Ports;

/// <summary>Non-persistent store (tests / heads without storage). Default ON.</summary>
public sealed class InMemorySoundPreferencesStore : ISoundPreferencesStore
{
    private bool? _enabled;
    private bool? _goalNotificationsEnabled;

    public bool HasSavedPreferences => _enabled.HasValue || _goalNotificationsEnabled.HasValue;

    public bool LoadUiSoundsEnabled() => _enabled ?? true;

    public void SaveUiSoundsEnabled(bool enabled) => _enabled = enabled;

    public bool LoadGoalNotificationsEnabled() => _goalNotificationsEnabled ?? true;

    public void SaveGoalNotificationsEnabled(bool enabled) => _goalNotificationsEnabled = enabled;

    public void ClearSavedPreferences()
    {
        _enabled = null;
        _goalNotificationsEnabled = null;
    }
}
