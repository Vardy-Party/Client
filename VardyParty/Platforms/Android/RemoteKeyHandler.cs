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

    public bool HandleKeyDown(Keycode keyCode, KeyEvent? keyEvent)
    {
        Log.Info("RemoteKeyHandler", $"KeyDown - {keyCode}");

        switch (keyCode)
        {
            case Keycode.MediaPlayPause:
            case Keycode.MediaPlay:
            case Keycode.MediaPause:
                OnPlayPause?.Invoke(keyCode);
                return true;

            case Keycode.MediaStop:
                OnStop?.Invoke(keyCode);
                return true;

            case Keycode.MediaFastForward:
                OnFastForward?.Invoke(keyCode);
                return true;

            case Keycode.MediaRewind:
                OnRewind?.Invoke(keyCode);
                return true;

            case Keycode.MediaNext:
                OnNext?.Invoke(keyCode);
                return true;

            case Keycode.MediaPrevious:
                OnPrevious?.Invoke(keyCode);
                return true;

            case Keycode.ChannelUp:
                OnChannelUp?.Invoke(keyCode);
                return true;

            case Keycode.ChannelDown:
                OnChannelDown?.Invoke(keyCode);
                return true;

            case Keycode.VolumeUp:
                OnVolumeUp?.Invoke(keyCode);
                return false; // Let system handle volume

            case Keycode.VolumeDown:
                OnVolumeDown?.Invoke(keyCode);
                return false; // Let system handle volume

            case Keycode.VolumeMute:
                OnMute?.Invoke(keyCode);
                return false; // Let system handle mute

            case Keycode.Menu:
                OnMenu?.Invoke(keyCode);
                return true;

            case Keycode.Back:
                Log.Info("RemoteKeyHandler", "Back pressed (consumed)");
                if (OnBack != null)
                {
                    Log.Info("RemoteKeyHandler", "Invoking OnBack handler");
                    OnBack.Invoke(keyCode);
                    return true;
                }
                Log.Info("RemoteKeyHandler", "No OnBack handler attached");
                return false;

            case Keycode.Home:
                OnHome?.Invoke(keyCode);
                return false; // Let system handle home

            case Keycode.Button1:
            case Keycode.ProgYellow:
                OnYellow?.Invoke(keyCode);
                return true;

            case Keycode.DpadCenter:
            case Keycode.Enter:
            case Keycode.NumpadEnter:
                OnEnter?.Invoke(keyCode);
                if (ShouldConsumeEnter?.Invoke() == true)
                {
                    return true;
                }

                // Assist TV WebView click (DPAD_CENTER often never reaches the page as Enter).
                // TryClick is fire-and-forget (must not block UI thread). Consume when scheduled
                // so the key isn't also handled elsewhere; Blazor debounce guards double-toggle.
                if (global::VardyParty.MauiProgram.IsTv
                    && global::VardyParty.MainPage.Instance?.TryClickFocusedWebElement() == true)
                {
                    return true;
                }

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
