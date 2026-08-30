namespace VardyParty.Presentation;

/// <summary>
/// Phone launcher paints splash, then starts MAUI. Do not hand off after
/// the activity is finishing or destroyed (Back during the wait, process
/// death). Android types stay out of this type so the rules are unit tested.
/// </summary>
public static class PhoneSplashHandoff
{
    public static bool ShouldAdvertisePhoneLauncher(bool isTelevisionDevice) =>
        !isTelevisionDevice;

    /// <summary>
    /// Leanback must not stay enabled on phones. Several phone launchers
    /// hide packages that advertise <c>LEANBACK_LAUNCHER</c>, so the app
    /// installs but never appears in the drawer.
    /// </summary>
    public static bool ShouldAdvertiseTvLeanbackLauncher(bool isTelevisionDevice) =>
        isTelevisionDevice;

    public static bool ShouldBuildMaui(bool alreadyHandedOff, bool isFinishing, bool isDestroyed) =>
        !alreadyHandedOff && !isFinishing && !isDestroyed;

    public static bool ShouldStartMainActivity(bool mauiStarted, bool isFinishing, bool isDestroyed) =>
        mauiStarted && !isFinishing && !isDestroyed;

    /// <summary>
    /// The MAUI host must be built on the Android main thread. Start it only
    /// after a splash frame has been submitted <em>and</em> the looper is
    /// idle, so the system has presented splash before the ~2s host build.
    /// Do not abandon the host on a timer — a half-initialized MAUI app is
    /// worse than a slow splash.
    /// </summary>
    public static bool ShouldBuildMauiOnLooperIdle(bool splashFrameSubmitted, bool looperIdle) =>
        splashFrameSubmitted && looperIdle;
}
