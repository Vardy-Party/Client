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

        return new Window(page)
        {
            Title = "Vardy Party",
            Width = 1280,
            Height = 800,
        };
    }
}
