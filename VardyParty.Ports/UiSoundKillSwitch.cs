namespace VardyParty.Ports;

/// <summary>
/// Startup kill switch for all platform sound players: set
/// VARDYPARTY_NO_SOUND=1 and every composition root registers
/// <see cref="NullUiSoundPlayer"/> instead of its native player (mirrors the
/// VARDYPARTY_NO_CHROME window-chrome switch on Windows), so startup crashes
/// can be bisected: chrome vs sound vs both.
/// </summary>
public static class UiSoundKillSwitch
{
    public const string VariableName = "VARDYPARTY_NO_SOUND";

    /// <summary>Evaluated once per process, at first touch during registration.</summary>
    public static bool IsDisabled { get; } = IsDisabledValue(ReadVariable());

    /// <summary>Only the exact value "1" disables sounds.</summary>
    public static bool IsDisabledValue(string? environmentValue) => environmentValue?.Trim() == "1";

    private static string? ReadVariable()
    {
        try
        {
            return Environment.GetEnvironmentVariable(VariableName);
        }
        catch
        {
            // Reading the environment must never break startup.
            return null;
        }
    }
}
