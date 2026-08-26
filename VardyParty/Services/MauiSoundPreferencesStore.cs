using Microsoft.Maui.Storage;
using VardyParty.Ports;

namespace VardyParty.MauiServices;

/// <summary>MAUI Preferences-backed store for the "UI sounds" toggle. Default ON.</summary>
public class MauiSoundPreferencesStore : ISoundPreferencesStore
{
    private const string PreferencesKey = "ui_sounds_enabled";

    public bool HasSavedPreferences => Preferences.ContainsKey(PreferencesKey);

    public bool LoadUiSoundsEnabled() => Preferences.Get(PreferencesKey, true);

    public void SaveUiSoundsEnabled(bool enabled) => Preferences.Set(PreferencesKey, enabled);

    public void ClearSavedPreferences() => Preferences.Remove(PreferencesKey);
}
