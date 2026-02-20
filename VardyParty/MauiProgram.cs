using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VardyParty.Components.Pages;
using VardyParty.Configuration;
using VardyParty.Exceptions;
using VardyParty.Handlers;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Orchestrators;
using VardyParty.Parsers;
#if ANDROID
using VardyParty.Platforms.Android;
#endif
using VardyParty.Providers;
using VardyParty.Resolvers;
using VardyParty.Services;

namespace VardyParty;

public static class MauiProgram
{
    // Set by Android startup to indicate TV devices
    public static bool IsTv { get; set; } = false;

    // Set by Android startup to indicate whether a usable WebView implementation is present
    public static bool IsWebViewAvailable { get; set; } = false;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"); });
        builder.Configuration
            .AddJsonFile("appsettings.json", true)
            .AddSecrets(Assembly.GetExecutingAssembly())
            .AddEnvironmentVariables();


        // NOTE: Previously we loaded appsettings.json synchronously here which can block the UI thread
        // on slow devices. Defer loading of appsettings until after the app is built by warming the
        // IAppSettingsProvider on a background thread. Services that need configuration should use
        // IAppSettingsProvider to obtain values asynchronously.

        // Only add BlazorWebView when the platform actually has a working WebView implementation.
        // For Android TV, runtime checks set IsWebViewAvailable; for other platforms assume available.
        var isAndroid = OperatingSystem.IsAndroid();
        if (!isAndroid || IsWebViewAvailable)
        {
            Console.WriteLine("[MauiProgram] WebView available or non-Android - registering BlazorWebView");
            builder.Services.AddMauiBlazorWebView();
        }
        else
        {
            Console.WriteLine("[MauiProgram] Android WebView unavailable or disabled - registering stub/fallback");
#if ANDROID
            try
            {
                builder.ConfigureMauiHandlers(handlers =>
                {
                    handlers.AddHandler(typeof(BlazorWebView),
                        typeof(StubBlazorWebViewHandler));
                });
                Console.WriteLine("[MauiProgram] Registered fallback handler for BlazorWebView");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MauiProgram] Failed to register fallback handler: {ex.Message}");
            }
#endif
        }

#if ANDROID
        builder.Services.AddSingleton<INativeVideoPlayerService, AndroidVideoPlayerService>();
#elif WINDOWS
            builder.Services.AddSingleton<INativeVideoPlayerService, Platforms.Windows.WindowsVideoPlayerService>();
            // Register windows overlay close service for native close control in overlay
            builder.Services.AddSingleton<VardyParty.Services.IOverlayCloseService, VardyParty.Platforms.Windows.OverlayCloseService>();
#elif IOS
            builder.Services.AddSingleton<INativeVideoPlayerService, Platforms.iOS.IosVideoPlayerService>();
#elif MACCATALYST
            builder.Services.AddSingleton<INativeVideoPlayerService, Platforms.MacCatalyst.MacCatalystVideoPlayerService>();
#endif

        builder.Services.AddTransient<M3U8HttpHandler>();

        builder.Services
            .BindConfiguration<APISettings>("Api")
            .BindConfiguration<GamesApiSettings>("GamesApi")
            .BindConfiguration<StreamHealthSettings>("StreamHealth")
            .BindConfiguration<Auth0Settings>("Auth0")
            .BindConfiguration<BbcFixturesSettings>("BbcFixtures");


        // Register AppSettings provider early so services can resolve it
        builder.Services
            .AddSingleton<IGameMatcher, GameMatcher>()
            .AddSingleton<IBbcJsonParser, BbcJsonParser>()
            .AddSingleton<IBbcHtmlParser, BbcHtmlParser>()
            .AddSingleton<IStreamDeduplicator, StreamDeduplicator>()
            .AddSingleton<IEnrichedGameService, EnrichedGameService>()
            .AddSingleton<IHomePagePresentationService, HomePagePresentationService>()
            .AddSingleton<IStreamSwitchingService, StreamSwitchingService>()
            .AddSingleton<IStreamSelectionCoordinator, StreamSelectionCoordinator>()
            .AddSingleton<IStreamResolutionOrchestrator, StreamResolutionOrchestrator>()
            .AddSingleton<IStreamHealthReporter, StreamHealthReporter>()
            .AddSingleton<ISessionIdProvider, SessionIdProvider>()
            .AddSingleton<ICastService, CastService>()
            .AddSingleton<IBuildInfoService, BuildInfoService>()
            .AddSingleton(DeviceInfo.Current)
            .AddSingleton<SelectionState>()
            .AddTransient<Home>()
            .AddTransient<VideoPlayer>();


        builder.Services.AddSingleton<Auth0AuthService>();
        builder.Services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<Auth0AuthService>());
        builder.Services.AddSingleton<IAuthLoginService>(sp => sp.GetRequiredService<Auth0AuthService>());
        builder.Services.AddTransient<Auth0ApiTokenHandler>();

        builder.Services.AddHttpClient<IStreamResolver, StreamResolver>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler();
#if DEBUG
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
#endif
                return handler;
            });

        builder.Services.AddHttpClient<IBbcFixturesService, BbcFixturesService>();
        builder.Services.AddHttpClient<IStreamHealthService, StreamHealthService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler();
#if DEBUG
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
#endif
                return handler;
            });
        builder.Services.AddHttpClient<IApiService, ApiService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler();
#if DEBUG
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
#endif
                return handler;
            });
        builder.Services.AddHttpClient<IStreamHealthChecker, StreamHealthChecker>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
        builder.Services.AddHttpClient("StreamApi")
            .AddHttpMessageHandler<M3U8HttpHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = true
            });
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        // Configure logging for all builds to diagnose startup issues
        builder.Logging
            .ClearProviders()
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Information);

        // Build the app first, then asynchronously warm configuration and other non-critical services off the UI thread.
        var app = builder.Build();

        // Capture the IServiceProvider for platform components that need to resolve services
        AppServiceProvider.ServiceProvider = app.Services;

        // Ensure session id is created at app startup
        _ = app.Services.GetService<ISessionIdProvider>();

        return app;
    }
}