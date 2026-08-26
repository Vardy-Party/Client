using System.Text.Json;
using VardyParty.Ports;

namespace VardyParty.Desktop.Services;

/// <summary>
/// JSON-file store in the per-user app-data directory. MAUI Preferences on the
/// Avalonia backend needs UseAvaloniaEssentials() which is unverified on this
/// preview; a plain file is dependable everywhere the desktop head runs.
/// </summary>
public sealed class FileSoundPreferencesStore : ISoundPreferencesStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileSoundPreferencesStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VardyParty.Desktop",
            "sound-preferences.json"))
    {
    }

    public FileSoundPreferencesStore(string path) => _path = path;

    public bool HasSavedPreferences => File.Exists(_path);

    public bool LoadUiSoundsEnabled()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return true;
                var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(_path));
                return model?.UiSoundsEnabled ?? true;
            }
            catch
            {
                return true; // unreadable file falls back to default ON
            }
        }
    }

    public void SaveUiSoundsEnabled(bool enabled)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new Model { UiSoundsEnabled = enabled }));
            }
            catch
            {
                // Persistence loss must never crash the UI toggle.
            }
        }
    }

    public void ClearSavedPreferences()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch
            {
            }
        }
    }

    private sealed class Model
    {
        public bool UiSoundsEnabled { get; set; } = true;
    }
}
