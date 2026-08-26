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

        var configuration = BuildConfiguration();
        builder.Configuration.AddConfiguration(configuration);

        builder.Services.Configure<Auth0Settings>(configuration.GetSection(Auth0Settings.SectionName));
        builder.Services.Configure<APISettings>(configuration.GetSection(APISettings.SectionName));
        builder.Services.Configure<GamesApiSettings>(configuration.GetSection(GamesApiSettings.SectionName));
        builder.Services.Configure<StreamHealthSettings>(configuration.GetSection(StreamHealthSettings.SectionName));
        builder.Services.Configure<BbcFixturesSettings>(configuration.GetSection(BbcFixturesSettings.SectionName));

        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton<ILeagueFilterPreferencesStore, InMemoryLeagueFilterPreferencesStore>();
        builder.Services.AddVardyParty();

        // Auth is stubbed in this preview head: the games API returns 401 and the
        // homepage shows its error banner until the shared device-code/PKCE login
        // is wired in (follow-up; see docs/architecture/homepage-maui-avalonia.md).
        builder.Services.AddSingleton<IAuthTokenProvider, StubAuthTokenProvider>();

        var apiSettings = configuration.GetSection(APISettings.SectionName).Get<APISettings>();
        builder.Services.AddVardyPartyHttpClients(apiSettings?.IgnoreSslCertificateErrors ?? false);

        builder.Services.AddSingleton<IHomeAssetLocator, DesktopHomeAssetLocator>();

        // UI sounds: registered per composition root (not in AddVardyParty).
        // Must precede AddVardyPartyHomeUi so its Null/in-memory TryAdd
        // fallbacks defer to these. Initialised in App after first render.
        builder.Services.AddSingleton<VardyParty.Ports.ISoundPreferencesStore, FileSoundPreferencesStore>();
        builder.Services.AddSingleton<VardyParty.Ports.IUiSoundPlayer, SoundFlowUiSoundPlayer>();

        builder.Services.AddVardyPartyHomeUi();
        builder.Services.AddSingleton<HomeFeed>();

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
