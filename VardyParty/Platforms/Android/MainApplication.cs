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
                return;
            }

            CallAndroidApplicationOnCreate();
        }

        /// <summary>
        /// Runs <see cref="MauiApplication.OnCreate"/>. Phone
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

        void CallAndroidApplicationOnCreate()
        {
            // MauiApplication.OnCreate is skipped on phones until the splash
            // has painted; still run Application.onCreate (empty in AOSP).
            var classRef = JNIEnv.FindClass("android/app/Application");
            try
            {
                var methodId = JNIEnv.GetMethodID(classRef, "onCreate", "()V");
                JNIEnv.CallNonvirtualVoidMethod(Handle, classRef, methodId);
            }
            finally
            {
                JNIEnv.DeleteLocalRef(classRef);
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
