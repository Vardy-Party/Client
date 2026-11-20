using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using Android.Widget;
using VardyParty.Presentation;

namespace VardyParty
{
    /// <summary>
    /// <see cref="MauiApplication.OnCreate"/> builds the MAUI host and must
    /// not run until an activity window can take focus. Phones defer via
    /// <see cref="SplashActivity"/> after the first splash frame; TV Leanback
    /// opens <see cref="MainActivity"/> directly, which calls
    /// <see cref="EnsureMauiApp"/> in its <c>OnCreate</c>.
    /// </summary>
    [Application]
    public class MainApplication : MauiApplication
    {
        bool _mauiStarted;

        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
            // ILLink has no managed callers for the phone launcher besides
            // the manifest. Keep the type so Release APKs still register
            // CATEGORY_LAUNCHER (otherwise the app installs with only
            // LEANBACK_LAUNCHER and phone drawers hide it).
            _ = typeof(SplashActivity);
        }

        protected override MauiApp CreateMauiApp()
        {
            DetectTv();
            AndroidEnvironment.UnhandledExceptionRaiser += OnUnhandledException;
            return MauiProgram.CreateMauiApp();
        }

        public override void OnCreate()
        {
            try
            {
                RegisterActivityLifecycleCallbacks(new Platforms.Android.ActivityInjectionCallbacks());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainApplication] Failed to register activity injection callbacks: {ex}");
            }

            DetectTv();
            // No task is known yet — assume one is rooted at Splash so we never
            // disable it here. MainActivity reapplies from the real task roots.
            ApplyLauncherComponents(anyAppTaskRootedAtSplash: true);

            // Never build the MAUI host here — on TV or phone. Application.OnCreate
            // runs before any activity window exists; a multi-second EnsureMauiApp
            // here is an ANR ("no focused window"). Phones paint SplashActivity
            // first; TV Leanback opens MainActivity, which calls EnsureMauiApp
            // after the activity theme window is up. AOSP Application onCreate is
            // empty; skipping the JNI hop also avoids CheckJNI DeleteLocalRef.
        }

        /// <summary>
        /// Runs <see cref="MauiApplication.OnCreate"/> once. Phone
        /// <see cref="SplashActivity"/> calls this after the first splash frame.
        /// </summary>
        public void EnsureMauiApp()
        {
            if (_mauiStarted)
            {
                return;
            }

            _mauiStarted = true;
            base.OnCreate();
        }

        /// <summary>
        /// Aligns Splash and TvLeanbackAlias enabled state with the device.
        /// Only calls <c>SetComponentEnabledSetting</c> when the effective
        /// state differs. Never disables Splash while a task of this app is
        /// rooted at it: on Android 9+ that finishes the task
        /// (<c>disabled-package</c>) and sends the user to the TV home
        /// screen. Application.OnCreate therefore defers the Splash disable.
        /// MainActivity reapplies from <see cref="ReadAppTaskRootClassNames"/>.
        /// Trade-off: Splash stays advertised (CATEGORY_LAUNCHER) on TV
        /// until a later Leanback launch. Android TV homes typically list
        /// only LEANBACK_LAUNCHER, so a duplicate tile is unlikely for that
        /// first session; GetLaunchIntentForPackage may still resolve to
        /// Splash until the disable runs.
        /// </summary>
        internal void ApplyLauncherComponents(bool anyAppTaskRootedAtSplash)
        {
            try
            {
                var pm = PackageManager;
                if (pm == null)
                {
                    return;
                }

                var isTv = MauiProgram.IsTv;
                var splash = new ComponentName("com.vardyparty", LauncherComponentRules.PhoneLauncherClassName);
                var tvAlias = new ComponentName("com.vardyparty", LauncherComponentRules.TvLeanbackAliasClassName);

                var splashEnabled = LauncherComponentRules.IsPhoneLauncherEnabled(
                    (int)pm.GetComponentEnabledSetting(splash));
                var aliasEnabled = LauncherComponentRules.IsTvLeanbackAliasEnabled(
                    (int)pm.GetComponentEnabledSetting(tvAlias));

                if (LauncherComponentRules.ShouldApplyPhoneLauncherState(
                    isTv, anyAppTaskRootedAtSplash, splashEnabled))
                {
                    var splashState = LauncherComponentRules.DesiredPhoneLauncherEnabled(isTv)
                        ? ComponentEnabledState.Enabled
                        : ComponentEnabledState.Disabled;
                    pm.SetComponentEnabledSetting(splash, splashState, ComponentEnableOption.DontKillApp);
                    Console.WriteLine($"[MainApplication] Splash launcher set to {splashState}");
                }
                else if (isTv
                    && !LauncherComponentRules.DesiredPhoneLauncherEnabled(isTv)
                    && !LauncherComponentRules.CanDisablePhoneLauncher(anyAppTaskRootedAtSplash))
                {
                    Console.WriteLine("[MainApplication] Deferring Splash disable while a task is rooted at SplashActivity");
                }

                if (LauncherComponentRules.ShouldApplyTvLeanbackLauncherState(isTv, aliasEnabled))
                {
                    var tvState = LauncherComponentRules.DesiredTvLeanbackLauncherEnabled(isTv)
                        ? ComponentEnabledState.Enabled
                        : ComponentEnabledState.Disabled;
                    pm.SetComponentEnabledSetting(tvAlias, tvState, ComponentEnableOption.DontKillApp);
                    Console.WriteLine($"[MainApplication] TvLeanbackAlias set to {tvState}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainApplication] Launcher component update failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Root component class name of each of this app's tasks
        /// (<c>ActivityManager.AppTasks</c> → <c>TaskInfo.BaseIntent.Component</c>,
        /// else <c>BaseActivity</c>), the same component the Android 9+
        /// <c>disabled-package</c> cleanup matches. <see langword="null"/>
        /// entries mean an unknown root; a <see langword="null"/> result means
        /// the tasks could not be read. Both defer the Splash disable.
        /// </summary>
        internal IReadOnlyList<string?>? ReadAppTaskRootClassNames()
        {
            try
            {
                if (GetSystemService(ActivityService) is not ActivityManager activityManager)
                {
                    return null;
                }

                var roots = new List<string?>();
                foreach (var task in activityManager.AppTasks ?? [])
                {
                    roots.Add(ReadTaskRoot(task));
                }

                Console.WriteLine($"[MainApplication] App task roots: {string.Join(", ", roots.Select(r => r ?? "<unknown>"))}");
                return roots;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("MainApplication", $"Could not read app tasks; deferring Splash disable: {ex.Message}");
                return null;
            }
        }

        static string? ReadTaskRoot(ActivityManager.AppTask task)
        {
            try
            {
                var info = task.TaskInfo;
                if (info is null)
                {
                    return null;
                }

                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    // API 29 moved these fields to TaskInfo.
                    Android.App.TaskInfo taskInfo = info;
                    var root29 = taskInfo.BaseIntent?.Component?.ClassName;
                    return string.IsNullOrEmpty(root29) ? taskInfo.BaseActivity?.ClassName : root29;
                }

                var root = info.BaseIntent?.Component?.ClassName;
                if (string.IsNullOrEmpty(root) && OperatingSystem.IsAndroidVersionAtLeast(23))
                {
                    root = info.BaseActivity?.ClassName;
                }

                return root;
            }
            catch (Exception ex)
            {
                // The task can go away between AppTasks and TaskInfo.
                Android.Util.Log.Warn("MainApplication", $"Could not read an app task root: {ex.Message}");
                return null;
            }
        }

        void DetectTv()
        {
            try
            {
                var pm = PackageManager
                    ?? throw new InvalidOperationException("PackageManager is unavailable.");
                var hasLeanback = pm.HasSystemFeature(Android.Content.PM.PackageManager.FeatureLeanback);
#pragma warning disable CS0618 // FeatureTelevision is the pre-Leanback TV flag still present on older images
                var hasTelevision = pm.HasSystemFeature(Android.Content.PM.PackageManager.FeatureTelevision);
#pragma warning restore CS0618
                var isTv = hasLeanback || hasTelevision;
                MauiProgram.IsTv = isTv;
                Console.WriteLine($"[MainApplication] Device IsTv={isTv}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainApplication] TV detection failed: {ex.Message}");
            }
        }

        private void OnUnhandledException(object? sender, RaiseThrowableEventArgs e)
        {
            // Media3 IDataSource.Open/Read throw Java.IO.IOException across JNI.
            // .NET Android still raises UnhandledExceptionRaiser for that even
            // when ExoPlayer handles the error. Reloading the app turns every
            // segment I/O hiccup into a crash loop.
            if (IsMedia3JniIoException(e.Exception))
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            Console.WriteLine($"[CRASH] Unhandled Exception: {e.Exception}");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    Context context = Platform.CurrentActivity ?? Android.App.Application.Context;
                    Toast.MakeText(context, "An error occurred. Reloading...", ToastLength.Long)?.Show();

                    var intent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
                    if (intent != null)
                    {
                        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.NewTask);
                        StartActivity(intent);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during crash handling: {ex}");
                }
            });
        }

        private static bool IsMedia3JniIoException(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is Java.IO.IOException)
                    return true;

                var stack = current.StackTrace;
                if (stack is not null
                    && (stack.Contains("ManagedHttpDataSource", StringComparison.Ordinal)
                        || stack.Contains("IDataReaderInvoker", StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
