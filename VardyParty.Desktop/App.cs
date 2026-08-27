using VardyParty.Streaming;

namespace VardyParty.Desktop;

public class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Auth, games feed, stream resolution and playback are all wired
        // inside DesktopHomePage (mirrors HomeHostPage on the MAUI head).
        var page = _services.GetRequiredService<Pages.DesktopHomePage>();

        // LAN local-service availability monitoring runs for the app lifetime.
        _services.GetService<ILocalLanServiceAvailabilityMonitor>()?.Start();

        var window = new Window(page)
        {
            Title = "Vardy Party",
            Width = 1280,
            Height = 800,
        };
        WireForegroundState(window);
        return window;
    }

    /// <summary>
    /// Match-event notifications follow the window lifecycle (same mapping
    /// as the MAUI head's App): visible = foregrounded; Stopped/minimized
    /// delivers nothing; Deactivated (e.g. the native VLC window taking
    /// focus during playback) keeps notifications flowing so the
    /// "playing → toast only" policy row still delivers. If the Avalonia
    /// preview backend never raises Stopped, the state simply stays
    /// foregrounded — the pre-feature behaviour.
    /// </summary>
    private void WireForegroundState(Window window)
    {
        var notifications = _services.GetRequiredService<VardyParty.Presentation.MatchEventNotificationPolicy>();
        window.Activated += (_, _) => notifications.IsAppForegrounded = true;
        window.Resumed += (_, _) => notifications.IsAppForegrounded = true;
        window.Stopped += (_, _) => notifications.IsAppForegrounded = false;
        window.Destroying += (_, _) => notifications.IsAppForegrounded = false;
    }
}
