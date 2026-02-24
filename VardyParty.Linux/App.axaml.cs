using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
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

namespace VardyParty.Linux;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LibVLCSharp.Shared.Core.Initialize();

        var appSettingsPath = ResolveAppSettingsPath();
        var appSettingsDirectory = Path.GetDirectoryName(appSettingsPath)!;
        var appSettingsFileName = Path.GetFileName(appSettingsPath);

        var allowUserSecrets = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
            .Build()
            .GetValue("AllowUserSecrets", false);

        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(appSettingsDirectory)
            .AddJsonFile(appSettingsFileName, optional: false, reloadOnChange: true)
            .AddEnvironmentVariables();

        if (allowUserSecrets)
        {
            configurationBuilder.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
        }

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
        services.AddSingleton<LinuxAuthService>();
        services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<LinuxAuthService>());
        services.AddSingleton<IAuthLoginService>(sp => sp.GetRequiredService<LinuxAuthService>());
        services.AddTransient<Auth0ApiTokenHandler>();
        services.AddTransient<M3U8HttpHandler>();

        services.AddHttpClient<IStreamResolver, StreamResolver>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>();

        services.AddHttpClient<IBbcFixturesService, BbcFixturesService>();

        services.AddHttpClient<IStreamHealthService, StreamHealthService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>();

        services.AddHttpClient<IApiService, ApiService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>();

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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();
        }

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
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find appsettings.json. Checked: {string.Join(", ", candidates)}");
    }
}
