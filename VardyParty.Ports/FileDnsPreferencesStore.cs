using System.Text.Json;

namespace VardyParty.Ports;

/// <summary>
/// JSON-file store for the DoH fallback toggle. Desktop/Linux heads use this
/// when MAUI Preferences is unavailable.
/// </summary>
public sealed class FileDnsPreferencesStore : IDnsPreferencesStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileDnsPreferencesStore(string path) =>
        _path = path ?? throw new ArgumentNullException(nameof(path));

    public bool LoadDnsOverHttpsFallbackEnabled()
    {
        lock (_gate)
        {
            return Load().DnsOverHttpsFallbackEnabled;
        }
    }

    public void SaveDnsOverHttpsFallbackEnabled(bool enabled)
    {
        lock (_gate)
        {
            var model = Load();
            model.DnsOverHttpsFallbackEnabled = enabled;
            Save(model);
        }
    }

    private Model Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Model();
            return JsonSerializer.Deserialize<Model>(File.ReadAllText(_path)) ?? new Model();
        }
        catch
        {
            return new Model();
        }
    }

    private void Save(Model model)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(model));
        }
        catch
        {
            // Persistence loss must never crash the UI toggle.
        }
    }

    private sealed class Model
    {
        public bool DnsOverHttpsFallbackEnabled { get; set; } = true;
    }
}
