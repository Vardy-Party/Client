using System.Text.Json;

namespace VardyParty.Ports;

/// <summary>
/// JSON-file store for stream health data-collection opt-in (desktop/Linux).
/// </summary>
public sealed class FileStreamHealthDataCollectionPreferencesStore
    : IStreamHealthDataCollectionPreferencesStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileStreamHealthDataCollectionPreferencesStore(string path) =>
        _path = path ?? throw new ArgumentNullException(nameof(path));

    public bool LoadStreamHealthDataCollectionEnabled()
    {
        lock (_gate)
        {
            return Load().StreamHealthDataCollectionEnabled;
        }
    }

    public void SaveStreamHealthDataCollectionEnabled(bool enabled)
    {
        lock (_gate)
        {
            var model = Load();
            model.StreamHealthDataCollectionEnabled = enabled;
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
        public bool StreamHealthDataCollectionEnabled { get; set; }
    }
}
