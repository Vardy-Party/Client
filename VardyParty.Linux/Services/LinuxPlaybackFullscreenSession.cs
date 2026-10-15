namespace VardyParty.Linux.Services;

/// <summary>
/// Host-window modes for Linux playback fullscreen. Mapped to Avalonia
/// <c>WindowState</c> by <c>LinuxHomePage</c> — kept toolkit-free for unit tests.
/// </summary>
public enum LinuxHostWindowMode
{
    Normal,
    Maximized,
    FullScreen,
    Minimized,
}

/// <summary>
/// Escape after chrome layers are already dismissed
/// (<see cref="Presentation.PlaybackChromePresenter.TryDismissLayer"/> returned false).
/// </summary>
public enum LinuxPlaybackEscapeAction
{
    ExitFullscreen,
    ClosePlayback,
}

/// <summary>
/// Pure Escape-order helper: dismiss chrome layers (host), then exit
/// fullscreen, then close playback.
/// </summary>
public static class LinuxPlaybackEscapeOrder
{
    public static LinuxPlaybackEscapeAction Next(bool isFullscreenPlayback) =>
        isFullscreenPlayback
            ? LinuxPlaybackEscapeAction.ExitFullscreen
            : LinuxPlaybackEscapeAction.ClosePlayback;
}

/// <summary>
/// Tracks enter/exit of host-window fullscreen for embedded Linux playback.
/// Prefer shell <see cref="LinuxHostWindowMode.FullScreen"/> (not LibVLC-only)
/// so the Avalonia chrome overlay can still <c>PlaceOver</c> the video.
///
/// Native Ubuntu uses host <see cref="LinuxHostWindowMode.FullScreen"/>.
/// WSL (test host) defaults to Maximized because WSLg Weston often
/// ignores FullScreen. Override with
/// <c>VARDYPARTY_LINUX_FULLSCREEN_AS_MAXIMIZED=0|1</c>.
/// </summary>
public sealed class LinuxPlaybackFullscreenSession
{
    public const string MaximizeInsteadEnv = "VARDYPARTY_LINUX_FULLSCREEN_AS_MAXIMIZED";

    public bool IsFullscreen { get; private set; }

    /// <summary>Mode restored on exit (Normal or Maximized).</summary>
    public LinuxHostWindowMode RestoreMode { get; private set; } = LinuxHostWindowMode.Normal;

    /// <summary>
    /// Target mode when entering fullscreen playback.
    /// Native Ubuntu: FullScreen. WSL test host: Maximized (Weston).
    /// Env <c>1</c>/<c>true</c> forces Maximized; <c>0</c>/<c>false</c>
    /// forces FullScreen even on WSL.
    /// </summary>
    public static LinuxHostWindowMode ResolveEnterTarget(
        Func<string, string?>? getEnv = null,
        bool? isWsl = null)
    {
        getEnv ??= static name => Environment.GetEnvironmentVariable(name);
        var raw = getEnv(MaximizeInsteadEnv);
        if (IsFalsey(raw))
            return LinuxHostWindowMode.FullScreen;
        if (IsTruthy(raw))
            return LinuxHostWindowMode.Maximized;

        return (isWsl ?? LinuxPlatformProbe.IsWsl)
            ? LinuxHostWindowMode.Maximized
            : LinuxHostWindowMode.FullScreen;
    }

    public LinuxHostWindowMode Toggle(
        LinuxHostWindowMode current,
        Func<string, string?>? getEnv = null,
        bool? isWsl = null)
    {
        if (IsFullscreen)
            return Exit();

        return Enter(current, getEnv, isWsl);
    }

    public LinuxHostWindowMode Enter(
        LinuxHostWindowMode current,
        Func<string, string?>? getEnv = null,
        bool? isWsl = null)
    {
        if (IsFullscreen)
            return ResolveEnterTarget(getEnv, isWsl);

        RestoreMode = current is LinuxHostWindowMode.FullScreen or LinuxHostWindowMode.Minimized
            ? LinuxHostWindowMode.Normal
            : current;
        IsFullscreen = true;
        return ResolveEnterTarget(getEnv, isWsl);
    }

    public LinuxHostWindowMode Exit()
    {
        IsFullscreen = false;
        return RestoreMode;
    }

    public void Reset()
    {
        IsFullscreen = false;
        RestoreMode = LinuxHostWindowMode.Normal;
    }

    private static bool IsTruthy(string? raw) =>
        string.Equals(raw, "1", StringComparison.Ordinal) ||
        string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalsey(string? raw) =>
        string.Equals(raw, "0", StringComparison.Ordinal) ||
        string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
}
