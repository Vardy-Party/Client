using Avalonia.Controls.Maui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Desktop.Services;
using VardyParty.Hosting;
using VardyParty.HomeUi;
using VardyParty.Kernel;

namespace VardyParty.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseAvaloniaApp();

#if EMBEDDED_DESKTOP_VIDEO
        // In-window video playback: VideoHostView renders libvlc's output
        // inside the app window via LibVLCSharp.Avalonia's VideoView (see
        // Controls/VideoHostViewHandler). Compiled out (and the standalone
        // libvlc window used instead) with -p:EmbeddedDesktopVideo=false.
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<Controls.VideoHostView, Controls.VideoHostViewHandler>());
#endif

        var configuration = BuildConfiguration();
        builder.Configuration.AddConfiguration(configuration);

        builder.Services.Configure<Auth0Settings>(configuration.GetSection(Auth0Settings.SectionName));
        builder.Services.Configure<APISettings>(configuration.GetSection(APISettings.SectionName));
        builder.Services.Configure<GamesApiSettings>(configuration.GetSection(GamesApiSettings.SectionName));
        builder.Services.Configure<StreamHealthSettings>(configuration.GetSection(StreamHealthSettings.SectionName));
        builder.Services.Configure<BbcFixturesSettings>(configuration.GetSection(BbcFixturesSettings.SectionName));

        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VardyParty",
            "logs")));
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton<ILeagueFilterPreferencesStore, InMemoryLeagueFilterPreferencesStore>();
        builder.Services.AddVardyParty();

        // Real Auth0 session (device-code flow with QR, encrypted token cache),
        // ported from the retired VardyParty.Linux head.
        builder.Services.AddSingleton<DesktopAuthService>();
        builder.Services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<DesktopAuthService>());
        builder.Services.AddSingleton<IAuthLoginService>(sp => sp.GetRequiredService<DesktopAuthService>());

        var apiSettings = configuration.GetSection(APISettings.SectionName).Get<APISettings>();
        builder.Services.AddVardyPartyHttpClients(apiSettings?.IgnoreSslCertificateErrors ?? false);

        builder.Services.AddSingleton<IHomeAssetLocator, DesktopHomeAssetLocator>();

        // LibVLC playback in a dedicated native window (lazy init; see
        // DesktopVideoPlayerService for the Avalonia-12 surface rationale).
        builder.Services.AddSingleton<VardyParty.Playback.INativeVideoPlayerService, DesktopVideoPlayerService>();

        // UI sounds: registered per composition root (not in AddVardyParty).
        // Must precede AddVardyPartyHomeUi so its Null/in-memory TryAdd
        // fallbacks defer to these. Initialised after first render.
        // VARDYPARTY_NO_SOUND=1 (or the no-sound flag file) swaps in the null
        // player for crash bisecting.
        builder.Services.AddSingleton<VardyParty.Ports.ISoundPreferencesStore, FileSoundPreferencesStore>();
        if (VardyParty.Ports.UiSoundKillSwitch.IsDisabled)
        {
            Console.WriteLine($"[MauiProgram] UI sounds disabled via {VardyParty.Ports.UiSoundKillSwitch.Trigger} (NullUiSoundPlayer)");
            builder.Services.AddSingleton<VardyParty.Ports.IUiSoundPlayer, VardyParty.Ports.NullUiSoundPlayer>();
        }
        else
        {
            builder.Services.AddSingleton<VardyParty.Ports.IUiSoundPlayer, SoundFlowUiSoundPlayer>();
        }

        builder.Services.AddVardyPartyHomeUi();
        builder.Services.AddSingleton<Pages.DesktopHomePage>();

        return builder.Build();
    }

    private static IConfiguration BuildConfiguration()
    {
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables();

        // Point the head at another API deployment without editing appsettings:
        // VARDYPARTY_DESKTOP_API=local|preview|production
        var target = Environment.GetEnvironmentVariable("VARDYPARTY_DESKTOP_API")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(target))
        {
            var baseConfig = configurationBuilder.Build();
            var overrideUrl = target switch
            {
                "local" => baseConfig["Api:HeadlessBaseUrl-Local"],
                "preview" => baseConfig["Api:HeadlessBaseUrl-Preview"],
                _ => baseConfig["Api:HeadlessBaseUrl"],
            };
            if (!string.IsNullOrWhiteSpace(overrideUrl))
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:HeadlessBaseUrl"] = overrideUrl,
                });
            }
        }

        return configurationBuilder.Build();
    }
}
