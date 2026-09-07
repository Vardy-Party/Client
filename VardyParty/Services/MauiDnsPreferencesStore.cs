using Microsoft.Maui.Storage;
using VardyParty.Ports;

namespace VardyParty.MauiServices;

/// <summary>MAUI Preferences-backed DoH fallback toggle. Default ON.</summary>
public sealed class MauiDnsPreferencesStore : IDnsPreferencesStore
{
    private const string PreferencesKey = "dns_over_https_fallback_enabled";

    public bool LoadDnsOverHttpsFallbackEnabled() => Preferences.Get(PreferencesKey, true);

    public void SaveDnsOverHttpsFallbackEnabled(bool enabled) => Preferences.Set(PreferencesKey, enabled);
}
