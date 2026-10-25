using Microsoft.Extensions.Logging;
using VardyParty.Ports;

namespace VardyParty.Presentation;

/// <summary>
/// Settings policy for DNS over HTTPS (default ON). When enabled, internet
/// lookups prefer Cloudflare 1.1.1.1 and fall back to system DNS — same order
/// as phone CONNECT — so a hijacked or wrong system answer cannot skip DoH.
/// </summary>
public sealed class DnsOverHttpsPreference
{
    private readonly IDnsPreferencesStore _preferences;
    private readonly IEnumerable<ILocalLanDnsNotifier> _notifiers;
    private readonly ILogger<DnsOverHttpsPreference>? _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _notifyGate = new(1, 1);
    private bool? _enabledCache;
    private int _notifyGeneration;

    public DnsOverHttpsPreference(
        IDnsPreferencesStore preferences,
        IEnumerable<ILocalLanDnsNotifier>? notifiers = null,
        ILogger<DnsOverHttpsPreference>? logger = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _notifiers = notifiers ?? [];
        _logger = logger;
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
        var generation = Interlocked.Increment(ref _notifyGeneration);
        foreach (var notifier in _notifiers)
        {
            _ = NotifyOneAsync(notifier, enabled, generation);
        }
    }

    private async Task NotifyOneAsync(ILocalLanDnsNotifier notifier, bool enabled, int generation)
    {
        try
        {
            await _notifyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != Volatile.Read(ref _notifyGeneration))
                    return;

                await notifier.NotifyAsync(enabled).ConfigureAwait(false);
            }
            finally
            {
                _notifyGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to notify local service of DoH preference {Enabled}", enabled);
        }
    }
}
