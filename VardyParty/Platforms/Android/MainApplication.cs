using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Widget;

namespace VardyParty
{
    /// <summary>
    /// <see cref="MauiApplication.OnCreate"/> always builds the MAUI host
    /// before any activity can paint. Phones skip that until
    /// <see cref="SplashActivity"/> has drawn the splash; TV still starts
    /// MAUI here because Leanback launches <see cref="MainActivity"/>
    /// directly.
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

            if (MauiProgram.IsTv)
            {
                EnsureMauiApp();
            }

            // Phones: do not call Application.onCreate here. MauiApplication.OnCreate
            // builds the host (seconds) before any frame. SplashActivity paints,
            // then EnsureMauiApp runs the single super.OnCreate. AOSP Application
            // onCreate is empty; skipping the JNI hop avoids CheckJNI DeleteLocalRef
            // and a second onCreate.
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
    }
}
