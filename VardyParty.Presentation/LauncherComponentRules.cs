namespace VardyParty.Presentation;

/// <summary>
/// Policy for Android launcher component enablement (phone Splash /
/// TV Leanback alias). <c>PackageManager.SetComponentEnabledSetting</c> stays
/// in the platform adapter; this type is the decision logic.
/// </summary>
/// <remarks>
/// On Android 9+, disabling the component that started the current task
/// makes ActivityManager finish that task with reason <c>disabled-package</c>
/// and the user lands on the home screen. That happened on the Bravia after
/// a fresh 3.6.1-b262 install: first launch entered via SplashActivity,
/// <c>ApplyLauncherComponents</c> disabled Splash, and MainActivity was
/// finished at 20:00:14 Europe/London in the same process (PID 27132).
/// Never disable Splash in a process or launch that entered via Splash;
/// defer until a later launch through TvLeanbackAlias.
/// </remarks>
public static class LauncherComponentRules
{
    public const string PhoneLauncherClassName = "com.vardyparty.SplashActivity";
    public const string TvLeanbackAliasClassName = "com.vardyparty.TvLeanbackAlias";

    /// <summary>Android <c>COMPONENT_ENABLED_STATE_DEFAULT</c>.</summary>
    public const int ComponentStateDefault = 0;

    /// <summary>Android <c>COMPONENT_ENABLED_STATE_ENABLED</c>.</summary>
    public const int ComponentStateEnabled = 1;

    /// <summary>Android <c>COMPONENT_ENABLED_STATE_DISABLED</c>.</summary>
    public const int ComponentStateDisabled = 2;

    /// <summary>Android <c>COMPONENT_ENABLED_STATE_DISABLED_USER</c>.</summary>
    public const int ComponentStateDisabledUser = 3;

    /// <summary>Android <c>COMPONENT_ENABLED_STATE_DISABLED_UNTIL_USED</c>.</summary>
    public const int ComponentStateDisabledUntilUsed = 4;

    public static bool DesiredPhoneLauncherEnabled(bool isTelevisionDevice) =>
        PhoneSplashHandoff.ShouldAdvertisePhoneLauncher(isTelevisionDevice);

    public static bool DesiredTvLeanbackLauncherEnabled(bool isTelevisionDevice) =>
        PhoneSplashHandoff.ShouldAdvertiseTvLeanbackLauncher(isTelevisionDevice);

    /// <summary>
    /// Disabling Splash is only safe when this launch did not enter through
    /// SplashActivity. <c>Application.OnCreate</c> does not yet know the
    /// entry activity, so that caller must pass <c>true</c> (defer).
    /// </summary>
    public static bool CanDisablePhoneLauncher(bool launchEnteredViaSplash) =>
        !launchEnteredViaSplash;

    /// <summary>
    /// Resolves PackageManager's component enabled setting against the
    /// manifest default used when the setting is <c>DEFAULT</c>.
    /// </summary>
    public static bool IsEffectivelyEnabled(int componentEnabledState, bool manifestDefaultEnabled)
    {
        return componentEnabledState switch
        {
            ComponentStateEnabled => true,
            ComponentStateDisabled => false,
            ComponentStateDisabledUser => false,
            ComponentStateDisabledUntilUsed => false,
            _ => manifestDefaultEnabled,
        };
    }

    /// <summary>
    /// Whether to call <c>SetComponentEnabledSetting</c> for SplashActivity.
    /// False when already at the desired state, or when the desired state is
    /// disabled but this launch entered via Splash (leave Splash enabled
    /// for this session).
    /// </summary>
    public static bool ShouldApplyPhoneLauncherState(
        bool isTelevisionDevice,
        bool launchEnteredViaSplash,
        bool currentlyEnabled)
    {
        var desired = DesiredPhoneLauncherEnabled(isTelevisionDevice);
        if (!desired && !CanDisablePhoneLauncher(launchEnteredViaSplash))
        {
            return false;
        }

        return currentlyEnabled != desired;
    }

    /// <summary>
    /// Whether to call <c>SetComponentEnabledSetting</c> for TvLeanbackAlias.
    /// Enabling or disabling the alias does not finish a Splash-rooted task.
    /// </summary>
    public static bool ShouldApplyTvLeanbackLauncherState(
        bool isTelevisionDevice,
        bool currentlyEnabled) =>
        currentlyEnabled != DesiredTvLeanbackLauncherEnabled(isTelevisionDevice);

    /// <summary>
    /// A Leanback-alias intent did not enter via Splash, so it is safe to
    /// disable Splash (the current task's root is the alias). Any other
    /// component — including MainActivity after the phone-splash handoff,
    /// or a missing name in <c>Application.OnCreate</c> — is treated as
    /// entered-via-Splash so the disable is deferred.
    /// </summary>
    public static bool LaunchEnteredViaSplash(string? intentComponentClassName)
    {
        if (string.IsNullOrEmpty(intentComponentClassName))
        {
            return true;
        }

        return !string.Equals(
            intentComponentClassName,
            TvLeanbackAliasClassName,
            StringComparison.Ordinal);
    }
}
