using VardyParty.Ports;

namespace VardyParty.Presentation;

/// <summary>
/// Settings policy for "DNS over HTTPS fallback" (default ON). System DNS
/// first; Cloudflare 1.1.1.1 when host lookup fails.
/// </summary>
public sealed class DnsOverHttpsPreference
{
    private readonly IDnsPreferencesStore _preferences;
    private readonly object _gate = new();
    private bool? _enabledCache;

    public DnsOverHttpsPreference(IDnsPreferencesStore preferences) =>
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabledCache ??= _preferences.LoadDnsOverHttpsFallbackEnabled();
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabledCache = enabled;
        }

        _preferences.SaveDnsOverHttpsFallbackEnabled(enabled);
    }
}
