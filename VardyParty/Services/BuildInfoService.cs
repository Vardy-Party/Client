using System.Globalization;
using System.Reflection;
using System.Text;

namespace VardyParty;

public record BuildInfo(string Version, string Commit, string BuiltAt, string BuiltAtFull);

public interface IBuildInfoService
{
    Task<BuildInfo> GetAsync();
}

public class BuildInfoService : IBuildInfoService
{
    private static readonly char[] BuildInfoSeparators = ['\r', '\n', ';'];

    private BuildInfo? _cache;

    public async Task<BuildInfo> GetAsync()
    {
        if (_cache != null)
        {
            return _cache;
        }

        var version = GetVersionLabel();
        var (commit, builtRaw) = await TryReadBuildInfoFileAsync();
        var (builtAt, builtAtFull) = FormatBuiltAt(builtRaw) ?? GetAssemblyBuiltAt();

        _cache = new BuildInfo(version, commit, builtAt, builtAtFull);
        return _cache;
    }

    /// <summary>
    /// Reads Commit/Built written by the GenerateBuildInfo MSBuild target at package time.
    /// Prefer this over assembly file timestamps — Android APK entries often show 1601-01-01 / 00:00 UTC.
    /// </summary>
    private static async Task<(string Commit, string? Built)> TryReadBuildInfoFileAsync()
    {
        var commit = "unknown";
        string? built = null;

        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("buildinfo.txt");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            foreach (var part in content.Split(BuildInfoSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;

                var key = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                if (key.Equals("Commit", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    commit = value;
                }
                else if (key.Equals("Built", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    built = value;
                }
            }
        }
        catch
        {
        }

        return (commit, built);
    }

    private static (string Short, string Full)? FormatBuiltAt(string? builtRaw)
    {
        if (string.IsNullOrWhiteSpace(builtRaw))
        {
            return null;
        }

        // Written by MSBuild as: yyyy-MM-dd HH:mm:ss UTC
        if (DateTime.TryParseExact(
                builtRaw.Replace(" UTC", "", StringComparison.OrdinalIgnoreCase).Trim(),
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utc)
            && IsPlausibleBuildTimestamp(utc))
        {
            return (utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        }

        if (DateTime.TryParse(builtRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc)
            && IsPlausibleBuildTimestamp(utc))
        {
            return (utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        }

        // Keep the packaged string if we can't parse it.
        return (builtRaw, builtRaw);
    }

    private static bool IsPlausibleBuildTimestamp(DateTime utc) => utc.Year >= 2020;

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
            var path = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(path))
            {
                return ("unknown", "unknown");
            }

            var utc = File.GetLastWriteTimeUtc(path);
            if (!IsPlausibleBuildTimestamp(utc))
            {
                // Android APK / AOT assemblies often report Windows FILETIME epoch (1601-01-01).
                return ("unknown", "unknown");
            }

            return (utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        }
        catch
        {
            return ("unknown", "unknown");
        }
    }
}
