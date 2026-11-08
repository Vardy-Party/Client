namespace VardyParty.Ports;

public sealed class InMemoryStreamHealthDataCollectionPreferencesStore
    : IStreamHealthDataCollectionPreferencesStore
{
    private bool _enabled;

    public bool LoadStreamHealthDataCollectionEnabled() => _enabled;

    public void SaveStreamHealthDataCollectionEnabled(bool enabled) => _enabled = enabled;
}
