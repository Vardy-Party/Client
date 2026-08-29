using Microsoft.Maui.Storage;
using VardyParty.Ports;

namespace VardyParty.MauiServices;

/// <summary>MAUI Preferences-backed store for the sound/notification toggles. Default ON.</summary>
public class MauiSoundPreferencesStore : ISoundPreferencesStore
{
    private const string PreferencesKey = "ui_sounds_enabled";
    private const string GoalNotificationsKey = "goal_notifications_enabled";

    public bool HasSavedPreferences =>
        Preferences.ContainsKey(PreferencesKey) || Preferences.ContainsKey(GoalNotificationsKey);

    public bool LoadUiSoundsEnabled() => Preferences.Get(PreferencesKey, true);

    public void SaveUiSoundsEnabled(bool enabled) => Preferences.Set(PreferencesKey, enabled);

    public bool LoadGoalNotificationsEnabled() => Preferences.Get(GoalNotificationsKey, true);

    public void SaveGoalNotificationsEnabled(bool enabled) => Preferences.Set(GoalNotificationsKey, enabled);

    public void ClearSavedPreferences()
    {
        Preferences.Remove(PreferencesKey);
        Preferences.Remove(GoalNotificationsKey);
    }
}
