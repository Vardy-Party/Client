using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using VardyParty.Presentation;

namespace VardyParty
{
    /// <summary>
    /// Phone launcher only. Native so the first frame can be the splash art
    /// before MAUI is built. TV keeps <see cref="MainActivity"/> as the
    /// Leanback launcher.
    /// </summary>
    [Activity(
        Theme = "@style/VardyParty.PhoneSplashTheme",
        MainLauncher = true,
        NoHistory = true,
        Exported = true,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLauncher })]
    public class SplashActivity : Activity
    {
        bool _handedOff;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var image = new ImageView(this);
            image.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#003090"));
            image.SetScaleType(ImageView.ScaleType.FitCenter);
            var drawableId = Resources?.GetIdentifier(
                "vardyparty_splash_generated", "drawable", PackageName) ?? 0;
            if (drawableId != 0)
            {
                image.SetImageResource(drawableId);
            }
            else
            {
                Log.Warn("SplashActivity", "[SPLASH] generated splash drawable missing");
            }

            SetContentView(image);
            Log.Info("SplashActivity", "[SPLASH] content set — waiting for first draw");

            var observer = image.ViewTreeObserver;
            if (observer != null && observer.IsAlive)
            {
                observer.AddOnDrawListener(new FirstDrawListener(this, image));
            }
            else
            {
                image.Post(HandoffToMaui);
            }
        }

        void HandoffToMaui()
        {
            if (!PhoneSplashHandoff.ShouldBuildMaui(_handedOff, IsFinishing, IsDestroyed))
            {
                return;
            }

            _handedOff = true;
            Log.Info("SplashActivity", "[SPLASH] first frame done — starting MAUI");

            var mauiStarted = false;
            try
            {
                ((MainApplication)MauiApplication.Current).EnsureMauiApp();
                mauiStarted = true;
            }
            catch (Exception ex)
            {
                Log.Error("SplashActivity", $"[SPLASH] EnsureMauiApp failed: {ex}");
                Finish();
                return;
            }

            if (!PhoneSplashHandoff.ShouldStartMainActivity(mauiStarted, IsFinishing, IsDestroyed))
            {
                return;
            }

            var intent = new Intent(this, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.NoAnimation);
            StartActivity(intent);
            Finish();
        }

        sealed class FirstDrawListener : Java.Lang.Object, ViewTreeObserver.IOnDrawListener
        {
            readonly SplashActivity _activity;
            readonly Android.Views.View _view;
            bool _fired;

            public FirstDrawListener(SplashActivity activity, Android.Views.View view)
            {
                _activity = activity;
                _view = view;
            }

            public void OnDraw()
            {
                if (_fired)
                {
                    return;
                }

                _fired = true;
                try
                {
                    _view.ViewTreeObserver?.RemoveOnDrawListener(this);
                }
                catch
                {
                }

                // Next looper message: the splash frame has been submitted.
                if (_activity.IsFinishing || _activity.IsDestroyed)
                {
                    return;
                }

                _view.Post(_activity.HandoffToMaui);
            }
        }
    }
}
