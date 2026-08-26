using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Widget;

namespace VardyParty
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        protected override MauiApp CreateMauiApp()
        {
            // Detect TV devices using PackageManager features
            try
            {
                var ctx = Android.App.Application.Context
                    ?? throw new InvalidOperationException("Application context is unavailable.");
                var pm = ctx.PackageManager
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

            AndroidEnvironment.UnhandledExceptionRaiser += OnUnhandledException;
            return MauiProgram.CreateMauiApp();
        }

        public override void OnCreate()
        {
            base.OnCreate();

            // Register lifecycle callbacks to perform constructor-like injection into activities
            try
            {
                RegisterActivityLifecycleCallbacks(new VardyParty.Platforms.Android.ActivityInjectionCallbacks());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainApplication] Failed to register activity injection callbacks: {ex}");
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

                    // Relaunch the homepage activity to restore stability.
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
