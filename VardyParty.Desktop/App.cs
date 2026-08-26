using VardyParty.HomeUi;
using VardyParty.HomeUi.Views;
using VardyParty.Kernel;
using VardyParty.Ports;

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

        // Stream resolution + playback wiring is a per-head concern; this
        // preview head confirms the pick until playback lands here.
        var viewModel = _services.GetRequiredService<HomeViewModel>();
        viewModel.GamePicked += game => OnGamePicked(page, game);

        // Start the games feed once the UI exists so updates land on a live page.
        _services.GetRequiredService<Services.HomeFeed>().Start();

        // Preload the UI sounds on a background task after the UI exists —
        // never in the startup path. Headless machines log-and-degrade.
        _ = Task.Run(() => _services.GetRequiredService<IUiSoundPlayer>().InitializeAsync());

        return new Window(page)
        {
            Title = "Vardy Party",
            Width = 1280,
            Height = 800,
        };
    }

    private static void OnGamePicked(Page page, Game game)
    {
        _ = page.DisplayAlertAsync(
            "Match selected",
            $"{game.DisplayHome} v {game.DisplayAway}\n\nPlayback is not wired into this preview head yet.",
            "OK");
    }
}
