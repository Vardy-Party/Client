using System.Reflection;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using VardyParty;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Components.Pages;
using VardyParty.Configuration;
using VardyParty.Exceptions;
using VardyParty.Hosting;
using VardyParty.Playback;
#if ANDROID
using VardyParty.Platforms.Android;
#endif
using VardyParty.MauiServices;
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

    private static bool AllowIgnoreSslCertificateErrors(APISettings apiSettings)
    {
#if !DEBUG
        if (apiSettings.IgnoreSslCertificateErrors)
        {
            Console.WriteLine("[MauiProgram] IgnoreSslCertificateErrors is ignored in Release builds");
            apiSettings.IgnoreSslCertificateErrors = false;
        }
#endif
        return apiSettings.IgnoreSslCertificateErrors;
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
        // Default to production; set VARDYPARTY_DEBUG_API=local|preview to override.
        var debugApiTarget = Environment.GetEnvironmentVariable("VARDYPARTY_DEBUG_API");
        var debugBaseUrl = debugApiTarget?.Trim().ToLowerInvariant() switch
        {
            "local" => builder.Configuration["Api:HeadlessBaseUrl-Local"],
            "preview" => builder.Configuration["Api:HeadlessBaseUrl-Preview"],
            "production" or "prod" => builder.Configuration["Api:HeadlessBaseUrl"],
            _ => builder.Configuration["Api:HeadlessBaseUrl"],
        };
        if (!string.IsNullOrWhiteSpace(debugBaseUrl))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HeadlessBaseUrl"] = debugBaseUrl
            });
            Console.WriteLine($"[MauiProgram] DEBUG: Using API at {debugBaseUrl} (target={debugApiTarget ?? "production"})");
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
            builder.Services.AddSingleton<IOverlayCloseService, VardyParty.Platforms.Windows.OverlayCloseService>();
#elif IOS
            builder.Services.AddSingleton<INativeVideoPlayerService, Platforms.iOS.IosVideoPlayerService>();
#elif MACCATALYST
            builder.Services.AddSingleton<INativeVideoPlayerService, Platforms.MacCatalyst.MacCatalystVideoPlayerService>();
#endif
        builder.Services.TryAddSingleton<IOverlayCloseService, NullOverlayCloseService>();

        builder.Services
            .BindConfiguration<APISettings>(APISettings.SectionName)
            .BindConfiguration<GamesApiSettings>(GamesApiSettings.SectionName)
            .BindConfiguration<StreamHealthSettings>(StreamHealthSettings.SectionName)
            .BindConfiguration<Auth0Settings>(Auth0Settings.SectionName)
            .BindConfiguration<BbcFixturesSettings>(BbcFixturesSettings.SectionName);

        builder.Services.AddSingleton<ILeagueFilterPreferencesStore, MauiLeagueFilterPreferencesStore>();
        builder.Services.AddVardyParty();
        builder.Services
            .AddSingleton<ICastService, CastService>()
            .AddSingleton<IBuildInfoService, BuildInfoService>()
            .AddSingleton(DeviceInfo.Current)
            .AddTransient<Home>()
            .AddTransient<VideoPlayer>();

        builder.Services.AddSingleton<Auth0AuthService>();
        builder.Services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<Auth0AuthService>());
        builder.Services.AddSingleton<IAuthLoginService>(sp => sp.GetRequiredService<Auth0AuthService>());
        builder.Services.AddVardyPartyHttpClients(AllowIgnoreSslCertificateErrors(apiSettings));
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