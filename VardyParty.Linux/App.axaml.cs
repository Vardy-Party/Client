using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Reflection;
using VardyParty.Services;
using VardyParty.Linux.Services;
using VardyParty.Configuration;
using VardyParty.Orchestrators;
using VardyParty.Providers;
using VardyParty.Resolvers;
using VardyParty.Parsers;
using VardyParty.Health;
using VardyParty.Handlers;

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
        // Initialize LibVLC
        LibVLCSharp.Shared.Core.Initialize();

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        // Setup Dependency Injection
        var services = new ServiceCollection();

        // Configuration
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<Auth0Settings>(configuration.GetSection(Auth0Settings.SectionName));
        services.Configure<APISettings>(configuration.GetSection(APISettings.SectionName));
        services.Configure<GamesApiSettings>(configuration.GetSection(GamesApiSettings.SectionName));
        services.Configure<StreamHealthSettings>(configuration.GetSection(StreamHealthSettings.SectionName));
        services.Configure<BbcFixturesSettings>(configuration.GetSection(BbcFixturesSettings.SectionName));

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // VardyParty.Core Services
        services.AddSingleton<IGameMatcher, GameMatcher>();
        services.AddSingleton<IBbcJsonParser, BbcJsonParser>();
        services.AddSingleton<IBbcHtmlParser, BbcHtmlParser>();
        services.AddSingleton<IStreamDeduplicator, StreamDeduplicator>();
        services.AddSingleton<IEnrichedGameService, EnrichedGameService>();
        services.AddSingleton<IHomePagePresentationService, HomePagePresentationService>();
        services.AddSingleton<IStreamSwitchingService, StreamSwitchingService>();
        services.AddSingleton<IStreamSelectionCoordinator, StreamSelectionCoordinator>();
        services.AddSingleton<IStreamResolutionOrchestrator, StreamResolutionOrchestrator>();
        services.AddSingleton<IStreamHealthService, StreamHealthService>();
        services.AddSingleton<IAuthTokenProvider, AuthTokenProvider>();
        services.AddSingleton<IAuthLoginService, AuthLoginService>();
        
        // HTTP Client with custom handler
        services.AddTransient<M3U8HttpHandler>();
        services.AddHttpClient<IEnrichedGameService, EnrichedGameService>()
            .ConfigurePrimaryHttpMessageHandler<M3U8HttpHandler>();

        // Selection State
        services.AddSingleton<SelectionState>();

        // Linux-specific Video Player Service
        services.AddSingleton<INativeVideoPlayerService, LinuxVideoPlayerService>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
