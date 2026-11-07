using Microsoft.Extensions.Logging;
using VardyParty.Ports;

namespace VardyParty.Presentation;

/// <summary>
/// Settings policy for anonymous stream health + scrape-health reporting (default OFF).
/// When disabled, the client neither POSTs playback <c>/health</c> nor sends
/// LocalService scrape-health context headers.
/// </summary>
public sealed class StreamHealthDataCollectionPreference
{
    private readonly IStreamHealthDataCollectionPreferencesStore _preferences;
    private readonly ILogger<StreamHealthDataCollectionPreference>? _logger;
    private readonly object _gate = new();
    private bool? _enabledCache;

    public StreamHealthDataCollectionPreference(
        IStreamHealthDataCollectionPreferencesStore preferences,
        ILogger<StreamHealthDataCollectionPreference>? logger = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger;
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabledCache ??= _preferences.LoadStreamHealthDataCollectionEnabled();
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _preferences.SaveStreamHealthDataCollectionEnabled(enabled);
            _enabledCache = enabled;
        }

        _logger?.LogInformation(
            "[StreamHealthDataCollection] Opt-in {State}",
            enabled ? "enabled" : "disabled");
    }
}