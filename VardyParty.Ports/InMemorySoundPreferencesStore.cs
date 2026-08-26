namespace VardyParty.Ports;

/// <summary>Non-persistent store (tests / heads without storage). Default ON.</summary>
public sealed class InMemorySoundPreferencesStore : ISoundPreferencesStore
{
    private bool? _enabled;

    public bool HasSavedPreferences => _enabled.HasValue;

    public bool LoadUiSoundsEnabled() => _enabled ?? true;

    public void SaveUiSoundsEnabled(bool enabled) => _enabled = enabled;

    public void ClearSavedPreferences() => _enabled = null;
}
