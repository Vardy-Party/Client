namespace VardyParty.MauiServices;

/// <summary>MAUI Preferences-backed stream health data-collection opt-in. Default OFF.</summary>
public sealed class MauiStreamHealthDataCollectionPreferencesStore
    : VardyParty.Ports.IStreamHealthDataCollectionPreferencesStore
{
    private const string PreferencesKey = "stream_health_data_collection_enabled";

    public bool LoadStreamHealthDataCollectionEnabled() => Preferences.Get(PreferencesKey, false);

    public void SaveStreamHealthDataCollectionEnabled(bool enabled) =>
        Preferences.Set(PreferencesKey, enabled);
}
