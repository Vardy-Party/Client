using VardyParty.Presentation;

namespace VardyParty.Hosting;

public sealed class FileDesktopPendingUpdateStore : IDesktopPendingUpdateStore
{
    public const string FileName = "pending-desktop-update.txt";

    private readonly string _path;

    public FileDesktopPendingUpdateStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VardyParty",
            FileName))
    {
    }

    public FileDesktopPendingUpdateStore(string path) =>
        _path = path ?? throw new ArgumentNullException(nameof(path));

    public AppReleaseVersion? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var text = File.ReadAllText(_path).Trim();
        return AppReleaseVersion.TryParseTag(text, out var version) ? version : null;
    }

    public void Write(AppReleaseVersion expected)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, expected.ToString());
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
