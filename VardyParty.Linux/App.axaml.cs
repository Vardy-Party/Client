using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VardyParty;
using VardyParty.Configuration;
using VardyParty.Handlers;
using VardyParty.Health;
using VardyParty.Linux.Services;
using VardyParty.Models;
using VardyParty.Orchestrators;
using VardyParty.Parsers;
using VardyParty.Providers;
using VardyParty.Resolvers;
using VardyParty.Services;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace VardyParty.Linux;

public class App : Application
{
    private const string LinuxUserSecretsId = "543d9e88-b60c-4397-bc9d-c4614b8b1dcb";
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize LibVLCSharp - requires libvlc from system
        // On Linux with classic confinement, this will use system-installed VLC
        Core.Initialize();

        var appSettingsPath = ResolveAppSettingsPath();
        var appSettingsDirectory = Path.GetDirectoryName(appSettingsPath)!;
        var appSettingsFileName = Path.GetFileName(appSettingsPath);

        var allowUserSecrets = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, false, false)
            .Build()
            .GetValue("AllowUserSecrets", false);

        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(appSettingsDirectory)
            .AddJsonFile(appSettingsFileName, false, true)
            .AddEnvironmentVariables();

        if (allowUserSecrets)
        {
            configurationBuilder.AddUserSecrets(Assembly.GetExecutingAssembly(), true);

            var userSecretsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".microsoft",
                "usersecrets",
                LinuxUserSecretsId,
                "secrets.json");

            configurationBuilder.AddJsonFile(userSecretsPath, true, true);
        }

#if DEBUG
        var previewBaseUrl = new ConfigurationBuilder()
            .SetBasePath(appSettingsDirectory)
            .AddJsonFile(appSettingsFileName, false, true)
            .Build()
            .GetSection("Api")["HeadlessBaseUrl-Preview"];
        if (!string.IsNullOrWhiteSpace(previewBaseUrl))
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HeadlessBaseUrl"] = previewBaseUrl
            });
            Console.WriteLine($"[App] DEBUG: Using preview API at {previewBaseUrl}");
        }
#endif

        var configuration = configurationBuilder.Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<Auth0Settings>(configuration.GetSection(Auth0Settings.SectionName));
        services.Configure<APISettings>(configuration.GetSection(APISettings.SectionName));
        services.Configure<GamesApiSettings>(configuration.GetSection(GamesApiSettings.SectionName));
        services.Configure<StreamHealthSettings>(configuration.GetSection(StreamHealthSettings.SectionName));
        services.Configure<BbcFixturesSettings>(configuration.GetSection(BbcFixturesSettings.SectionName));

        services.AddLogging(builder =>
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VardyParty",
                "logs");

            builder.AddProvider(new FileLoggerProvider(logDirectory));
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IGameMatcher, GameMatcher>();
        services.AddSingleton<IBbcJsonParser, BbcJsonParser>();
        services.AddSingleton<IBbcHtmlParser, BbcHtmlParser>();
        services.AddSingleton<IStreamDeduplicator, StreamDeduplicator>();
        services.AddSingleton<IEnrichedGameService, EnrichedGameService>();
        services.AddSingleton<IHomePagePresentationService, HomePagePresentationService>();
        services.AddSingleton<IStreamSwitchingService, StreamSwitchingService>();
        services.AddSingleton<IStreamSelectionCoordinator, StreamSelectionCoordinator>();
        services.AddSingleton<IStreamResolutionOrchestrator, StreamResolutionOrchestrator>();
        services.AddSingleton<IStreamHealthReporter, StreamHealthReporter>();
        services.AddSingleton<ISessionIdProvider, SessionIdProvider>();
        services.AddSingleton<LinuxAuthService>();
        services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<LinuxAuthService>());
        services.AddSingleton<IAuthLoginService>(sp => sp.GetRequiredService<LinuxAuthService>());
        services.AddTransient<Auth0ApiTokenHandler>();
        services.AddTransient<M3U8HttpHandler>();

        services.AddHttpClient<ILocalLanPlayService, LocalLanPlayService>();
        services.AddSingleton<ILocalLanServiceAvailabilityMonitor, LocalLanServiceAvailabilityMonitor>();
        services.AddSingleton<IStreamResolver, StreamResolver>();

        services.AddHttpClient<IBbcFixturesService, BbcFixturesService>();

        services.AddHttpClient<IStreamHealthService, StreamHealthService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>();
        services.AddHttpClient<IApiService, ApiService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    VardyPartyClientApiVersion.HeaderName,
                    VardyPartyClientApiVersion.DefaultHeaderValue);
            });

        services.AddHttpClient<IStreamHealthChecker, StreamHealthChecker>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });

        services.AddHttpClient("StreamApi")
            .AddHttpMessageHandler<M3U8HttpHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });

        services.AddSingleton<SelectionState>();
        services.AddSingleton<INativeVideoPlayerService, LinuxVideoPlayerService>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();
        Services.GetService<ILocalLanServiceAvailabilityMonitor>()?.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();

        base.OnFrameworkInitializationCompleted();
    }

    private static string ResolveAppSettingsPath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var baseDirectory = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            Path.Combine(currentDirectory, "appsettings.json"),
            Path.Combine(currentDirectory, "VardyParty", "appsettings.json"),
            Path.Combine(baseDirectory, "appsettings.json"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "VardyParty", "appsettings.json"))
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        throw new FileNotFoundException($"Could not find appsettings.json. Checked: {string.Join(", ", candidates)}");
    }
}