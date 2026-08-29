namespace VardyParty.Ports;

/// <summary>
/// Startup kill switch for all platform sound players: set
/// VARDYPARTY_NO_SOUND=1 — or create the flag file
/// <c>%LOCALAPPDATA%\VardyParty\flags\no-sound</c> (see
/// <see cref="StartupFlagFiles"/>; the file path works for packaged MSIX apps
/// where terminal environment variables never arrive) — and every composition
/// root registers <see cref="NullUiSoundPlayer"/> instead of its native player
/// (mirrors the VARDYPARTY_NO_CHROME window-chrome switch on Windows), so
/// startup crashes can be bisected: chrome vs sound vs both.
/// </summary>
public static class UiSoundKillSwitch
{
    public const string VariableName = "VARDYPARTY_NO_SOUND";

    public const string FlagFileName = "no-sound";

    /// <summary>Evaluated once per process, at first touch during registration.</summary>
    public static bool IsDisabled => Trigger != null;

    /// <summary>
    /// Which mechanism enabled the switch ("environment variable …" or
    /// "flag file …"), for the registration log line; null when sounds are on.
    /// </summary>
    public static string? Trigger { get; } = DetectTrigger();

    /// <summary>Only the exact value "1" disables sounds.</summary>
    public static bool IsDisabledValue(string? environmentValue) => environmentValue?.Trim() == "1";

    private static string? DetectTrigger()
    {
        try
        {
            if (IsDisabledValue(Environment.GetEnvironmentVariable(VariableName)))
            {
                return $"environment variable {VariableName}=1";
            }
        }
        catch
        {
            // Reading the environment must never break startup.
        }

        var flagPath = StartupFlagFiles.Find(FlagFileName);
        return flagPath != null ? $"flag file {flagPath}" : null;
    }
}
