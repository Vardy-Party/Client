namespace VardyParty.Desktop.Services;

/// <summary>
/// Environment probes for the Linux/desktop head. WSL matters to playback:
/// WSLg's compositor and GPU paths have wedged libvlc in the field (a stuck
/// hardware-decode/vout probe froze the whole app), so
/// <see cref="DesktopVideoPlayerService"/> defaults WSL to conservative
/// libvlc options (software decode, plain X11 vout, no hardware probing) —
/// options that are also safe under headless xvfb.
/// </summary>
public static class DesktopPlatformProbe
{
    /// <summary>
    /// True when running under WSL: /proc/version contains "microsoft"
    /// (case-insensitive; covers both WSL1 "Microsoft" and WSL2
    /// "microsoft-standard" kernels).
    /// </summary>
    public static bool IsWsl { get; } = DetectWsl();

    /// <summary>
    /// VARDYPARTY_DESKTOP_VLC_SAFE=1 forces the same conservative libvlc
    /// option set WSL gets, on any machine — a diagnostic/test hook (used by
    /// the headless xvfb verification, and handy when a desktop's VA-API/GL
    /// stack misbehaves).
    /// </summary>
    public static bool ForceSafeVlcOptions =>
        Environment.GetEnvironmentVariable("VARDYPARTY_DESKTOP_VLC_SAFE") == "1";

    private static bool DetectWsl()
    {
        try
        {
            return File.Exists("/proc/version") &&
                   File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
