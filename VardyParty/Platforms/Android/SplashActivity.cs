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
    /// Leanback launcher (<c>show_phone_launcher</c> is false on
    /// television/leanback).
    /// </summary>
    [Activity(
        Name = "com.vardyparty.SplashActivity",
        Theme = "@style/VardyParty.PhoneSplashTheme",
        MainLauncher = true,
        NoHistory = true,
        Exported = true,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLauncher })]
    public class SplashActivity : Activity
    {
        bool _handedOff;
        bool _splashFrameSubmitted;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            if (!PhoneSplashHandoff.ShouldAdvertisePhoneLauncher(MauiProgram.IsTv))
            {
                Log.Info("SplashActivity", "[SPLASH] television device — skip phone launcher, start MainActivity");
                StartMauiThenMainActivity();
                return;
            }

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
                ScheduleHandoffAfterPresentedFrame(image);
            }
        }

        void ScheduleHandoffAfterPresentedFrame(Android.Views.View view)
        {
            view.Post(() =>
            {
                if (IsFinishing || IsDestroyed)
                {
                    return;
                }

                _splashFrameSubmitted = true;
                try
                {
                    ReportFullyDrawn();
                }
                catch (Exception ex)
                {
                    Log.Warn("SplashActivity", $"[SPLASH] ReportFullyDrawn failed: {ex.Message}");
                }

                var queue = Looper.MyQueue();
                if (queue == null)
                {
                    HandoffToMaui();
                    return;
                }

                queue.AddIdleHandler(new AfterFrameIdleHandler(this));
            });
        }

        void HandoffToMaui()
        {
            if (!PhoneSplashHandoff.ShouldBuildMauiOnLooperIdle(_splashFrameSubmitted, looperIdle: true))
            {
                return;
            }

            StartMauiThenMainActivity();
        }

        void StartMauiThenMainActivity()
        {
            if (!PhoneSplashHandoff.ShouldBuildMaui(_handedOff, IsFinishing, IsDestroyed))
            {
                return;
            }

            _handedOff = true;
            Log.Info("SplashActivity", "[SPLASH] starting MAUI");

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

        sealed class AfterFrameIdleHandler : Java.Lang.Object, MessageQueue.IIdleHandler
        {
            readonly SplashActivity _activity;

            public AfterFrameIdleHandler(SplashActivity activity) => _activity = activity;

            public bool QueueIdle()
            {
                _activity.HandoffToMaui();
                return false;
            }
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

                if (_activity.IsFinishing || _activity.IsDestroyed)
                {
                    return;
                }

                _activity.ScheduleHandoffAfterPresentedFrame(_view);
            }
        }
    }
}
