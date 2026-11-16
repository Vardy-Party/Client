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
            // Entry activity is not known yet — assume Splash so we never
            // disable it here. MainActivity reapplies once the intent is known.
            ApplyLauncherComponents(launchEnteredViaSplash: true);

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
        /// state differs. Never disables Splash in a launch that entered
        /// through it: on Android 9+ that finishes the foreground task
        /// (<c>disabled-package</c>) and sends the user to the TV home
        /// screen. Application.OnCreate therefore defers the Splash disable.
        /// MainActivity reapplies when the intent is TvLeanbackAlias.
        /// Trade-off: Splash stays advertised (CATEGORY_LAUNCHER) on TV
        /// until a later Leanback launch. Android TV homes typically list
        /// only LEANBACK_LAUNCHER, so a duplicate tile is unlikely for that
        /// first session; GetLaunchIntentForPackage may still resolve to
        /// Splash until the disable runs.
        /// </summary>
        internal void ApplyLauncherComponents(bool launchEnteredViaSplash)
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

                var splashEnabled = LauncherComponentRules.IsEffectivelyEnabled(
                    (int)pm.GetComponentEnabledSetting(splash),
                    manifestDefaultEnabled: true);
                var aliasEnabled = LauncherComponentRules.IsEffectivelyEnabled(
                    (int)pm.GetComponentEnabledSetting(tvAlias),
                    manifestDefaultEnabled: ResolveShowTvLauncherDefault());

                if (LauncherComponentRules.ShouldApplyPhoneLauncherState(
                    isTv, launchEnteredViaSplash, splashEnabled))
                {
                    var splashState = LauncherComponentRules.DesiredPhoneLauncherEnabled(isTv)
                        ? ComponentEnabledState.Enabled
                        : ComponentEnabledState.Disabled;
                    pm.SetComponentEnabledSetting(splash, splashState, ComponentEnableOption.DontKillApp);
                    Console.WriteLine($"[MainApplication] Splash launcher set to {splashState}");
                }
                else if (isTv
                    && !LauncherComponentRules.DesiredPhoneLauncherEnabled(isTv)
                    && !LauncherComponentRules.CanDisablePhoneLauncher(launchEnteredViaSplash))
                {
                    Console.WriteLine("[MainApplication] Deferring Splash disable until a launch that did not enter via SplashActivity");
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

        bool ResolveShowTvLauncherDefault()
        {
            try
            {
                var id = Resources?.GetIdentifier("show_tv_launcher", "bool", PackageName) ?? 0;
                if (id != 0)
                {
                    return Resources!.GetBoolean(id);
                }
            }
            catch
            {
            }

            return MauiProgram.IsTv;
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
