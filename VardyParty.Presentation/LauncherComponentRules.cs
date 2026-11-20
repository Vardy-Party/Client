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
/// The cleanup (<c>RecentTasks.cleanupDisabledPackageTasksLocked</c>) keys
/// on each task's root intent component, so the decision reads the real
/// task roots (<c>ActivityManager.AppTasks</c> base intent) rather than the
/// intent of the activity being created: never disable Splash while any of
/// this app's tasks is rooted at it.
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

    /// <summary>
    /// SplashActivity's <c>android:enabled</c> as declared (the <c>[Activity]</c> default).
    /// </summary>
    public const bool PhoneLauncherManifestDefaultEnabled = true;

    /// <summary>
    /// TvLeanbackAlias's <c>android:enabled</c> literal in AndroidManifest.xml. It must be
    /// a literal, never a configuration-qualified resource. PackageManager parses the
    /// manifest without the <c>television</c> UI-mode qualifier, so
    /// <c>@bool/show_tv_launcher</c> (true only in <c>values-television</c>) installed the
    /// alias disabled. The app read the same resource under the TV configuration, took
    /// the alias for enabled at <c>DEFAULT</c>, and never switched it on (3.6.2-b273: no
    /// LEANBACK_LAUNCHER activity on the Bravia). With a literal false the app always
    /// enables it explicitly on TV.
    /// </summary>
    public const bool TvLeanbackAliasManifestDefaultEnabled = false;
    public static bool DesiredPhoneLauncherEnabled(bool isTelevisionDevice) =>
        PhoneSplashHandoff.ShouldAdvertisePhoneLauncher(isTelevisionDevice);

    public static bool DesiredTvLeanbackLauncherEnabled(bool isTelevisionDevice) =>
        PhoneSplashHandoff.ShouldAdvertiseTvLeanbackLauncher(isTelevisionDevice);

    /// <summary>
    /// Disabling Splash is only safe when no task of this app is rooted at
    /// SplashActivity. <c>Application.OnCreate</c> runs before any task is
    /// known, so that caller must pass <c>true</c> (defer).
    /// </summary>
    public static bool CanDisablePhoneLauncher(bool anyAppTaskRootedAtSplash) =>
        !anyAppTaskRootedAtSplash;

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

    /// <summary>Whether SplashActivity is enabled, for PackageManager's setting.</summary>
    public static bool IsPhoneLauncherEnabled(int componentEnabledState) =>
        IsEffectivelyEnabled(componentEnabledState, PhoneLauncherManifestDefaultEnabled);

    /// <summary>Whether TvLeanbackAlias is enabled, for PackageManager's setting.</summary>
    public static bool IsTvLeanbackAliasEnabled(int componentEnabledState) =>
        IsEffectivelyEnabled(componentEnabledState, TvLeanbackAliasManifestDefaultEnabled);

    /// <summary>
    /// Whether to call <c>SetComponentEnabledSetting</c> for SplashActivity.    /// False when already at the desired state, or when the desired state is
    /// disabled but a task of this app is still rooted at Splash (leave
    /// Splash enabled until no task depends on it).
    /// </summary>
    public static bool ShouldApplyPhoneLauncherState(
        bool isTelevisionDevice,
        bool anyAppTaskRootedAtSplash,
        bool currentlyEnabled)
    {
        var desired = DesiredPhoneLauncherEnabled(isTelevisionDevice);
        if (!desired && !CanDisablePhoneLauncher(anyAppTaskRootedAtSplash))
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
    /// Whether any of this app's tasks is rooted at SplashActivity, from the
    /// root component class name of each task (<c>ActivityManager.AppTasks</c>
    /// → <c>TaskInfo.BaseIntent.Component</c>, else <c>BaseActivity</c>).
    /// Conservative: when the tasks could not be read (<see langword="null"/>),
    /// none were returned (the caller's own task should always be there), or
    /// a task's root is unknown, Splash is treated as a root so the disable
    /// is deferred. A task rooted at TvLeanbackAlias or MainActivity is safe.
    /// </summary>
    public static bool AnyAppTaskRootedAtSplash(IReadOnlyCollection<string?>? appTaskRootClassNames)
    {
        if (appTaskRootClassNames is null || appTaskRootClassNames.Count == 0)
        {
            return true;
        }

        foreach (var root in appTaskRootClassNames)
        {
            if (string.IsNullOrEmpty(root)
                || string.Equals(root, PhoneLauncherClassName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
