using System.Text.Json;
using VardyParty.Ports;

namespace VardyParty.Linux.Services;

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
            "VardyParty.Linux",
            "sound-preferences.json"))
    {
    }

    public FileSoundPreferencesStore(string path) => _path = path;

    public bool HasSavedPreferences => File.Exists(_path);

    public bool LoadUiSoundsEnabled()
    {
        lock (_gate)
        {
            return Load().UiSoundsEnabled;
        }
    }

    public void SaveUiSoundsEnabled(bool enabled)
    {
        lock (_gate)
        {
            var model = Load();
            model.UiSoundsEnabled = enabled;
            Save(model);
        }
    }

    public bool LoadGoalNotificationsEnabled()
    {
        lock (_gate)
        {
            return Load().GoalNotificationsEnabled;
        }
    }

    public void SaveGoalNotificationsEnabled(bool enabled)
    {
        lock (_gate)
        {
            var model = Load();
            model.GoalNotificationsEnabled = enabled;
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
            return new Model(); // unreadable file falls back to defaults (ON)
        }
    }

    private void Save(Model model)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(model));
        }
        catch
        {
            // Persistence loss must never crash the UI toggle.
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

        public bool GoalNotificationsEnabled { get; set; } = true;
    }
}
