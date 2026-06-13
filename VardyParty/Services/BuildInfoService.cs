using System.Reflection;
using System.Text;

namespace VardyParty.Services;

public record BuildInfo(string Version, string Commit, string BuiltAt, string BuiltAtFull);

public interface IBuildInfoService
{
    Task<BuildInfo> GetAsync();
}

public class BuildInfoService : IBuildInfoService
{
    private BuildInfo? _cache;

    public async Task<BuildInfo> GetAsync()
    {
        if (_cache != null)
        {
            return _cache;
        }

        var version = GetVersionLabel();
        // DLL last-write time reflects the binary actually running (including AppX sync).
        var (builtAt, builtAtFull) = GetAssemblyBuiltAt();
        var commit = await TryReadCommitAsync();

        _cache = new BuildInfo(version, commit, builtAt, builtAtFull);
        return _cache;
    }

    private static async Task<string> TryReadCommitAsync()
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("buildinfo.txt");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            foreach (var part in content.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0].Equals("Commit", StringComparison.OrdinalIgnoreCase))
                {
                    return kv[1].Trim();
                }
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static string GetVersionLabel()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var display = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = display.IndexOf('+');
        if (plus > 0)
        {
            display = display[..plus];
        }

        var build = assembly.GetName().Version?.Major;
        return build is > 0 ? $"{display} ({build})" : display;
    }

    private static (string Short, string Full) GetAssemblyBuiltAt()
    {
        try
        {
            var path = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(path))
            {
                return ("unknown", "unknown");
            }

            var utc = File.GetLastWriteTimeUtc(path);
            return (utc.ToString("HH:mm 'UTC'"), utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        }
        catch
        {
            return ("unknown", "unknown");
        }
    }
}
