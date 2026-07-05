using System.Net;
using System.Net.Security;
using System.Reflection;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VardyParty;
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
using VardyParty.MauiServices;
using VardyParty.Services;
#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.Maui.LifecycleEvents;
using VardyParty.Platforms.Windows;
using WinUiWindow = Microsoft.UI.Xaml.Window;
#endif

namespace VardyParty;

public static class MauiProgram
{
    // Set by Android startup to indicate TV devices
    public static bool IsTv { get; set; } = false;

    public static bool IsWindowsPackaged => _isWindowsPackaged;

    private static readonly bool _isWindowsPackaged = DetectWindowsPackaged();

    private static bool DetectWindowsPackaged()
    {
#if WINDOWS
        try
        {
            _ = Windows.ApplicationModel.Package.Current.Id.FullName;
            return true;
        }
        catch
        {
            return false;
        }
#else
        return true;
#endif
    }

    // Set by Android startup to indicate whether a usable WebView implementation is present
    public static bool IsWebViewAvailable { get; set; } = false;

    private static HttpClientHandler CreateHeadlessHttpClientHandler(APISettings apiSettings)
    {
        var handler = new HttpClientHandler();

        if (!apiSettings.IgnoreSslCertificateErrors)
        {
            return handler;
        }

        if (!Uri.TryCreate(apiSettings.HeadlessBaseUrl, UriKind.Absolute, out var headlessUri) ||
            string.IsNullOrWhiteSpace(headlessUri.Host))
        {
            return handler;
        }

        var allowedHost = headlessUri.Host;

        handler.ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            var host = request?.RequestUri?.Host;
            return !string.IsNullOrWhiteSpace(host) &&
                   string.Equals(host, allowedHost, StringComparison.OrdinalIgnoreCase);
        };

        return handler;
    }

    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        WindowsEventLogger.Info("MauiProgram", "CreateMauiApp starting");
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"); });

#if WINDOWS
        builder.ConfigureLifecycleEvents(events =>
        {
            events.AddWindows(windows =>
            {
                windows.OnWindowCreated(window => WindowsWindowChrome.ApplyMainWindowChrome(window));
                windows.OnActivated((window, _) => WindowsWindowChrome.ApplyMainWindowChrome(window));
            });
        });

        WindowHandler.Mapper.ModifyMapping(nameof(IWindow.Content), (handler, view, action) =>
        {
            if (handler.PlatformView is WinUiWindow nativeWindow)
            {
                WindowsWindowChrome.PrepareBeforeMauiConnect(nativeWindow);
            }

            action?.Invoke(handler, view);

            if (handler.PlatformView is WinUiWindow connectedWindow)
            {
                WindowsWindowChrome.ApplyMainWindowChrome(connectedWindow, handler.MauiContext);
            }
        });

        WindowHandler.Mapper.ModifyMapping(nameof(IWindow.Title), (handler, view, action) =>
        {
            if (handler.PlatformView is WinUiWindow nativeWindow)
            {
                WindowsWindowChrome.ApplyMainWindowChrome(nativeWindow, handler.MauiContext);
            }
        });

        WindowHandler.Mapper.ModifyMapping(nameof(IWindow.TitleBar), (handler, view, action) =>
        {
            action?.Invoke(handler, view);

            if (handler.PlatformView is WinUiWindow nativeWindow)
            {
                WindowsWindowChrome.ApplyMainWindowChrome(nativeWindow, handler.MauiContext);
            }
        });
#endif
        
        // Load appsettings.json from embedded resources (works on all platforms: Android, iOS, macOS, MSIX, etc.)
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("VardyParty.appsettings.json");
            if (stream != null)
            {
                builder.Configuration.AddJsonStream(stream);
            }
            else
            {
                Console.WriteLine("[MauiProgram] Warning: appsettings.json not found in embedded resources");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiProgram] Error loading embedded appsettings.json: {ex.Message}");
        }
        
        builder.Configuration.AddSecrets(Assembly.GetExecutingAssembly());
#if DEBUG
        // Default to local wrangler for dev; set VARDYPARTY_DEBUG_API=preview to hit preview workers.
        var debugApiTarget = Environment.GetEnvironmentVariable("VARDYPARTY_DEBUG_API");
        var debugBaseUrl = debugApiTarget?.Trim().ToLowerInvariant() switch
        {
            "preview" => builder.Configuration["Api:HeadlessBaseUrl-Preview"],
            "production" or "prod" => builder.Configuration["Api:HeadlessBaseUrl"],
            _ => builder.Configuration["Api:HeadlessBaseUrl-Local"],
        };
        if (!string.IsNullOrWhiteSpace(debugBaseUrl))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HeadlessBaseUrl"] = debugBaseUrl
            });
            Console.WriteLine($"[MauiProgram] DEBUG: Using API at {debugBaseUrl} (target={debugApiTarget ?? "local"})");
        }
#endif
        var apiSettings = builder.Configuration.GetSection(APISettings.SectionName).Get<APISettings>()
                          ?? throw new InvalidOperationException("Missing Api configuration section.");

        // Only add BlazorWebView when the platform actually has a working WebView implementation.
        // For Android TV, runtime checks set IsWebViewAvailable; for other platforms assume available.
#if ANDROID
        if (IsWebViewAvailable)
        {
            Console.WriteLine("[MauiProgram] WebView available - registering BlazorWebView");
            builder.Services.AddMauiBlazorWebView();
        }
        else
        {
            Console.WriteLine("[MauiProgram] Android WebView unavailable or disabled - registering stub/fallback");
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
        }
#else
        // Non-Android platforms always have BlazorWebView available
        Console.WriteLine("[MauiProgram] WebView available - registering BlazorWebView");
        builder.Services.AddMauiBlazorWebView();
#endif

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
            .BindConfiguration<APISettings>(APISettings.SectionName)
            .BindConfiguration<GamesApiSettings>(GamesApiSettings.SectionName)
            .BindConfiguration<StreamHealthSettings>(StreamHealthSettings.SectionName)
            .BindConfiguration<Auth0Settings>(Auth0Settings.SectionName)
            .BindConfiguration<BbcFixturesSettings>(BbcFixturesSettings.SectionName);

        // Register AppSettings provider early so services can resolve it
        builder.Services
            .AddSingleton<IGameMatcher, GameMatcher>()
            .AddSingleton<IBbcJsonParser, BbcJsonParser>()
            .AddSingleton<IBbcHtmlParser, BbcHtmlParser>()
            .AddSingleton<IStreamDeduplicator, StreamDeduplicator>()
            .AddSingleton<IEnrichedGameService, EnrichedGameService>()
            .AddSingleton<ILeagueFilterPreferencesStore, MauiLeagueFilterPreferencesStore>()
            .AddSingleton<ILeagueFilterService, LeagueFilterService>()
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

        builder.Services.AddHttpClient<ILocalLanPlayService, LocalLanPlayService>();
        builder.Services.AddSingleton<ILocalLanServiceAvailabilityMonitor, LocalLanServiceAvailabilityMonitor>();
        builder.Services.AddSingleton<IStreamResolver, StreamResolver>();

        builder.Services.AddHttpClient<IBbcFixturesService, BbcFixturesService>();
        builder.Services.AddHttpClient<IStreamHealthService, StreamHealthService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => CreateHeadlessHttpClientHandler(apiSettings));
        builder.Services.AddHttpClient<IApiService, ApiService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => CreateHeadlessHttpClientHandler(apiSettings))
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    VardyPartyClientApiVersion.HeaderName,
                    VardyPartyClientApiVersion.DefaultHeaderValue);
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

#if WINDOWS
        var windowsLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VardyParty",
            "logs");
        var windowsFileLogger = new WindowsFileLoggerProvider(windowsLogDir);
        builder.Logging.AddProvider(windowsFileLogger);
        WindowsEventLogger.RegisterFilePath(windowsFileLogger.FilePath);
        WindowsEventLogger.Info("Startup", $"Windows log file: {windowsFileLogger.FilePath}");
#endif

        // Build the app first, then asynchronously warm configuration and other non-critical services off the UI thread.
        var app = builder.Build();

        // Validate required configuration sections exist (fail fast if CD/CD merge failed)
        var logger = app.Services.GetRequiredService<ILogger<App>>();
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        ConfigurationValidator.ValidateConfiguration(configuration, logger);

        // Capture the IServiceProvider for platform components that need to resolve services
        AppServiceProvider.ServiceProvider = app.Services;

        // Ensure session id is created at app startup
        _ = app.Services.GetService<ISessionIdProvider>();

        // Start LAN local-service availability monitoring at startup.
        app.Services.GetService<ILocalLanServiceAvailabilityMonitor>()?.Start();

        return app;
    }
}