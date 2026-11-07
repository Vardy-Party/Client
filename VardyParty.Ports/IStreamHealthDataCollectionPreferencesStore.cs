namespace VardyParty.Ports;

/// <summary>
/// Persists the opt-in for anonymous stream health / scrape-health telemetry.
/// Default is OFF — no Worker health posts and no LocalService scrape context
/// until the user enables it in Settings.
/// </summary>
public interface IStreamHealthDataCollectionPreferencesStore
{
    /// <summary>Saved value, or false when nothing was saved (opt-in).</summary>
    bool LoadStreamHealthDataCollectionEnabled();

    void SaveStreamHealthDataCollectionEnabled(bool enabled);
}
