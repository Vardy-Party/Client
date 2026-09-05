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
/// WSLg: set <c>VARDYPARTY_LINUX_FULLSCREEN_AS_MAXIMIZED=1</c> to enter
/// Maximized instead of FullScreen when the compositor is flaky.
/// </summary>
public sealed class LinuxPlaybackFullscreenSession
{
    public const string MaximizeInsteadEnv = "VARDYPARTY_LINUX_FULLSCREEN_AS_MAXIMIZED";

    public bool IsFullscreen { get; private set; }

    /// <summary>Mode restored on exit (Normal or Maximized).</summary>
    public LinuxHostWindowMode RestoreMode { get; private set; } = LinuxHostWindowMode.Normal;

    /// <summary>
    /// Target mode when entering fullscreen playback. FullScreen by default;
    /// Maximized when the WSLg degrade env is set.
    /// </summary>
    public static LinuxHostWindowMode ResolveEnterTarget(
        Func<string, string?>? getEnv = null)
    {
        getEnv ??= static name => Environment.GetEnvironmentVariable(name);
        var raw = getEnv(MaximizeInsteadEnv);
        if (string.Equals(raw, "1", StringComparison.Ordinal) ||
            string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxHostWindowMode.Maximized;
        }

        return LinuxHostWindowMode.FullScreen;
    }

    public LinuxHostWindowMode Toggle(
        LinuxHostWindowMode current,
        Func<string, string?>? getEnv = null)
    {
        if (IsFullscreen)
            return Exit();

        return Enter(current, getEnv);
    }

    public LinuxHostWindowMode Enter(
        LinuxHostWindowMode current,
        Func<string, string?>? getEnv = null)
    {
        if (IsFullscreen)
            return ResolveEnterTarget(getEnv);

        RestoreMode = current is LinuxHostWindowMode.FullScreen or LinuxHostWindowMode.Minimized
            ? LinuxHostWindowMode.Normal
            : current;
        IsFullscreen = true;
        return ResolveEnterTarget(getEnv);
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
}
