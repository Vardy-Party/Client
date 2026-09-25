using VardyParty.Ports;

namespace VardyParty.Presentation;

/// <summary>
/// Settings policy for "DNS over HTTPS fallback" (default ON). System DNS
/// first; Cloudflare 1.1.1.1 when host lookup fails.
/// </summary>
public sealed class DnsOverHttpsPreference
{
    private readonly IDnsPreferencesStore _preferences;
    private readonly IEnumerable<ILocalLanDnsNotifier> _notifiers;
    private readonly object _gate = new();
    private bool? _enabledCache;

    public DnsOverHttpsPreference(
        IDnsPreferencesStore preferences,
        IEnumerable<ILocalLanDnsNotifier>? notifiers = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _notifiers = notifiers ?? [];
    }

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
        NotifyLocalService(enabled);
    }

    private void NotifyLocalService(bool enabled)
    {
        foreach (var notifier in _notifiers)
        {
            _ = NotifyOneAsync(notifier, enabled);
        }
    }

    private static async Task NotifyOneAsync(ILocalLanDnsNotifier notifier, bool enabled)
    {
        try
        {
            await notifier.NotifyAsync(enabled).ConfigureAwait(false);
        }
        catch
        {
            // The notifier logs its own failure. The toggle must still persist.
        }
    }
}
