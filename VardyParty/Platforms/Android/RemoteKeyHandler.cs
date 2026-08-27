using Android.Util;
using Android.Views;

namespace VardyParty.Platforms.Android;

/// <summary>
/// Handles remote control key events for Android TV
/// </summary>
public class RemoteKeyHandler
{
    public delegate void KeyEventHandler(Keycode keyCode);

    public event KeyEventHandler? OnPlayPause;
    public event KeyEventHandler? OnStop;
    public event KeyEventHandler? OnFastForward;
    public event KeyEventHandler? OnRewind;
    public event KeyEventHandler? OnNext;
    public event KeyEventHandler? OnPrevious;
    public event KeyEventHandler? OnChannelUp;
    public event KeyEventHandler? OnChannelDown;
    public event KeyEventHandler? OnVolumeUp;
    public event KeyEventHandler? OnVolumeDown;
    public event KeyEventHandler? OnMute;
    public event KeyEventHandler? OnMenu;
    public event KeyEventHandler? OnBack;
    public event KeyEventHandler? OnHome;
    public event KeyEventHandler? OnYellow;
    public event KeyEventHandler? OnEnter;

    /// <summary>
    /// Allows callers to control whether Enter/DpadCenter should be consumed (e.g., when a popup is open).
    /// </summary>
    public Func<bool>? ShouldConsumeEnter { get; set; }

    /// <summary>
    /// Handlers run on the Activity's key-input path: an exception here is an
    /// UNHANDLED app crash (OnKeyDown has no catch, unlike OnBackPressed).
    /// Field report "Back with the menu open closed/crashed the app" — a
    /// throwing subscriber must never take the process down, and the key must
    /// still count as consumed so a failed close can't fall through to exit.
    /// Subscribers are isolated individually: MainActivity's navigation
    /// handler throwing must not starve HomeHostPage's menu-close handler
    /// later in the multicast (and vice versa).
    /// </summary>
    private static void SafeInvoke(KeyEventHandler? handler, Keycode keyCode, string name)
    {
        if (handler == null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((KeyEventHandler)subscriber).Invoke(keyCode);
            }
            catch (Exception ex)
            {
                Log.Error("RemoteKeyHandler", $"{name} handler threw: {ex}");
            }
        }
    }

    public bool HandleKeyDown(Keycode keyCode, KeyEvent? keyEvent)
    {
        Log.Info("RemoteKeyHandler", $"KeyDown - {keyCode}");

        switch (keyCode)
        {
            case Keycode.MediaPlayPause:
            case Keycode.MediaPlay:
            case Keycode.MediaPause:
                SafeInvoke(OnPlayPause, keyCode, nameof(OnPlayPause));
                return true;

            case Keycode.MediaStop:
                SafeInvoke(OnStop, keyCode, nameof(OnStop));
                return true;

            case Keycode.MediaFastForward:
                SafeInvoke(OnFastForward, keyCode, nameof(OnFastForward));
                return true;

            case Keycode.MediaRewind:
                SafeInvoke(OnRewind, keyCode, nameof(OnRewind));
                return true;

            case Keycode.MediaNext:
                SafeInvoke(OnNext, keyCode, nameof(OnNext));
                return true;

            case Keycode.MediaPrevious:
                SafeInvoke(OnPrevious, keyCode, nameof(OnPrevious));
                return true;

            case Keycode.ChannelUp:
                SafeInvoke(OnChannelUp, keyCode, nameof(OnChannelUp));
                return true;

            case Keycode.ChannelDown:
                SafeInvoke(OnChannelDown, keyCode, nameof(OnChannelDown));
                return true;

            case Keycode.VolumeUp:
                SafeInvoke(OnVolumeUp, keyCode, nameof(OnVolumeUp));
                return false; // Let system handle volume

            case Keycode.VolumeDown:
                SafeInvoke(OnVolumeDown, keyCode, nameof(OnVolumeDown));
                return false; // Let system handle volume

            case Keycode.VolumeMute:
                SafeInvoke(OnMute, keyCode, nameof(OnMute));
                return false; // Let system handle mute

            case Keycode.Menu:
                SafeInvoke(OnMenu, keyCode, nameof(OnMenu));
                return true;

            case Keycode.Back:
                Log.Info("RemoteKeyHandler", "Back pressed (consumed)");
                if (OnBack != null)
                {
                    Log.Info("RemoteKeyHandler", "Invoking OnBack handler");
                    SafeInvoke(OnBack, keyCode, nameof(OnBack));
                    return true;
                }
                Log.Info("RemoteKeyHandler", "No OnBack handler attached");
                return false;

            case Keycode.Home:
                SafeInvoke(OnHome, keyCode, nameof(OnHome));
                return false; // Let system handle home

            case Keycode.Button1:
            case Keycode.ProgYellow:
                SafeInvoke(OnYellow, keyCode, nameof(OnYellow));
                return true;

            case Keycode.DpadCenter:
            case Keycode.Enter:
            case Keycode.NumpadEnter:
                SafeInvoke(OnEnter, keyCode, nameof(OnEnter));
                if (ShouldConsumeEnter?.Invoke() == true)
                {
                    return true;
                }

                // Let the focused native view handle the click (MatchCardView
                // wires Click on its platform view for TV D-pad selection).
                return false;

            default:
                return false;
        }
    }

    public bool HandleKeyUp(Keycode keyCode, KeyEvent? keyEvent)
    {
        // Handle key up events if needed
        return false;
    }
}
