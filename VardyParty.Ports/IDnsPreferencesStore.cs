namespace VardyParty.Ports;

/// <summary>
/// Persists the "DNS over HTTPS fallback" toggle. Mirrors
/// <see cref="ISoundPreferencesStore"/>: heads plug in Preferences (MAUI) or a
/// file store; tests use in-memory.
/// </summary>
public interface IDnsPreferencesStore
{
    /// <summary>
    /// Returns the saved value, or TRUE when nothing was saved (default on —
    /// system DNS first, Cloudflare DoH when host lookup fails).
    /// </summary>
    bool LoadDnsOverHttpsFallbackEnabled();

    void SaveDnsOverHttpsFallbackEnabled(bool enabled);
}
