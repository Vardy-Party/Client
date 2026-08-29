namespace VardyParty.Ports;

/// <summary>
/// File-based startup kill-switch flags. Environment variables do not reach
/// packaged Windows (MSIX) apps launched via <c>shell:AppsFolder</c> — Explorer
/// activation does not inherit the terminal environment — so every startup
/// kill switch also honours a flag file: the mere presence of
/// <c>%LOCALAPPDATA%\VardyParty\flags\&lt;name&gt;</c> (the per-user
/// LocalApplicationData/ApplicationData equivalents on other platforms)
/// enables the switch. Contents are ignored; delete the file to re-enable.
/// </summary>
public static class StartupFlagFiles
{
    /// <summary>
    /// The first existing flag file for <paramref name="flagName"/>, or null.
    /// Never throws: flag probing must not be able to break startup.
    /// </summary>
    public static string? Find(string flagName)
    {
        try
        {
            return Find(flagName, CandidateFlagDirectories());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Testable core: probes <paramref name="flagDirectories"/> in order.</summary>
    public static string? Find(string flagName, IEnumerable<string> flagDirectories)
    {
        foreach (var directory in flagDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            try
            {
                var path = Path.Combine(directory, flagName);
                if (File.Exists(path)) return path;
            }
            catch
            {
                // An unreadable candidate must not stop the probe.
            }
        }

        return null;
    }

    /// <summary>
    /// Per-user app-data flag directories, most specific first:
    /// LocalApplicationData (= %LOCALAPPDATA% on Windows, the app files dir on
    /// Android, ~/.local/share on Linux) then ApplicationData (roaming /
    /// ~/.config), each under VardyParty/flags.
    /// </summary>
    public static IEnumerable<string> CandidateFlagDirectories()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                 })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, "VardyParty", "flags");
            }
        }
    }
}
