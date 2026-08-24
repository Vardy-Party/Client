#if ANDROID
using System;
using Android.App;
using Application = Android.App.Application;
using Android.OS;

namespace VardyParty.Platforms.Android
{
    class ActivityInjectionCallbacks : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        public void OnActivityCreated(Activity activity, Bundle? savedInstanceState)
        {
            try
            {
                AndroidActivityFactory.Inject(activity);
            }
            catch { }
        }

        public void OnActivityDestroyed(Activity activity) { }
        public void OnActivityPaused(Activity activity) { }
        public void OnActivityResumed(Activity activity) { }
        public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
        public void OnActivityStarted(Activity activity) { }
        public void OnActivityStopped(Activity activity) { }
    }
}
#endif
