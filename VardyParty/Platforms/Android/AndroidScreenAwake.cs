using Android.App;
using Android.Views;
using VardyParty.Platforms.Android;

namespace VardyParty;

/// <summary>
/// FLAG_KEEP_SCREEN_ON belongs to the Activity that is actually on screen.
/// HomeHostPage sets the homepage hold through <see cref="Set"/>; the native
/// player is a separate Activity, so <see cref="HoldPlayerWindow"/> owns that window.
/// </summary>
internal static class AndroidScreenAwake
{
    private static bool _holdHomepage;
    private static WeakReference<MainActivity>? _homepage;

    /// <summary>
    /// Remember the MAUI homepage Activity so finding-streams can keep that
    /// window awake even if <c>Platform.CurrentActivity</c> is already the player.
    /// </summary>
    public static void BindHomepage(MainActivity activity)
    {
        _homepage = new WeakReference<MainActivity>(activity);
        ApplyHomepage(activity);
    }

    public static void Set(bool on)
    {
        _holdHomepage = on;
        if (_homepage is not null && _homepage.TryGetTarget(out var homepage))
        {
            ApplyHomepage(homepage);
            return;
        }

        ApplyHomepage(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity);
    }

    /// <summary>
    /// NativeVideoActivity's own window. Homepage flags never reach this Activity.
    /// </summary>
    public static void HoldPlayerWindow(global::Android.Views.Window? window) =>
        ApplyWindow(window, on: true);

    private static void ApplyHomepage(Activity? activity)
    {
        // NativeVideoActivity holds its own window in OnCreate/OnResume.
        if (activity is NativeVideoActivity)
        {
            return;
        }

        ApplyWindow(activity?.Window, _holdHomepage);
    }

    private static void ApplyWindow(global::Android.Views.Window? window, bool on)
    {
        if (window is null)
        {
            return;
        }

        if (on)
        {
            window.AddFlags(WindowManagerFlags.KeepScreenOn);
        }
        else
        {
            window.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }

        if (window.DecorView is { } decor)
        {
            decor.KeepScreenOn = on;
        }
    }
}
