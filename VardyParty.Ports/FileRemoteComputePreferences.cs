using System.Text.Json;

namespace VardyParty.Ports;

public sealed class FileRemoteComputePreferences : IRemoteComputePreferences
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileRemoteComputePreferences(string path) =>
        _path = path ?? throw new ArgumentNullException(nameof(path));

    public bool LoadShareEnabled()
    {
        lock (_gate) return Load().ShareEnabled;
    }

    public void SaveShareEnabled(bool enabled)
    {
        lock (_gate)
        {
            var model = Load();
            model.ShareEnabled = enabled;
            Save(model);
        }
    }

    public string LoadInviteCode()
    {
        lock (_gate) return Load().InviteCode ?? "";
    }

    public void SaveInviteCode(string code)
    {
        lock (_gate)
        {
            var model = Load();
            model.InviteCode = code ?? "";
            Save(model);
        }
    }

    public long LoadInviteExpiresAt()
    {
        lock (_gate) return Load().InviteExpiresAt;
    }

    public void SaveInviteExpiresAt(long unixMilliseconds)
    {
        lock (_gate)
        {
            var model = Load();
            model.InviteExpiresAt = unixMilliseconds;
            Save(model);
        }
    }

    public string LoadPairedHostSub()
    {
        lock (_gate) return Load().PairedHostSub ?? "";
    }

    public void SavePairedHostSub(string hostSub)
    {
        lock (_gate)
        {
            var model = Load();
            model.PairedHostSub = hostSub ?? "";
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
            // Persistence loss must never crash the settings toggle.
        }
    }

    private sealed class Model
    {
        public bool ShareEnabled { get; set; }
        public string InviteCode { get; set; } = "";
        public long InviteExpiresAt { get; set; }
        public string PairedHostSub { get; set; } = "";
    }
}
