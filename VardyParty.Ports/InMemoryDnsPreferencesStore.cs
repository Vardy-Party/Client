namespace VardyParty.Ports;

/// <summary>Non-persistent store (tests / heads without storage). Default ON.</summary>
public sealed class InMemoryDnsPreferencesStore : IDnsPreferencesStore
{
    private bool? _enabled;

    public bool LoadDnsOverHttpsFallbackEnabled() => _enabled ?? true;

    public void SaveDnsOverHttpsFallbackEnabled(bool enabled) => _enabled = enabled;
}
