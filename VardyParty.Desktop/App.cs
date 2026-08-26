using VardyParty.HomeUi.Views;

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
        var page = _services.GetRequiredService<HomePage>();

        // Start the games feed once the UI exists so updates land on a live page.
        _services.GetRequiredService<Services.HomeFeed>().Start();

        return new Window(page)
        {
            Title = "Vardy Party",
            Width = 1280,
            Height = 800,
        };
    }
}
