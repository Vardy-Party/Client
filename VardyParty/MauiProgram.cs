using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using VardyParty;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;
using VardyParty.Hosting;
using VardyParty.Playback;
using VardyParty.Presentation;
using VardyParty.Streaming;
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
            .ConfigureFonts(fonts => { fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"); })
#if ANDROID
            .ConfigureMauiHandlers(handlers => HomeUi.Views.HomeUiCollectionView.Register(handlers))
#endif
            ;

#if WINDOWS
        // Every chrome hook is guarded: a chrome failure must never prevent the
        // window from showing (WinAppSDK 1.8 turns unhandled XAML-thread failures
        // into 0xc000027b stowed-exception crashes with no managed stack).
        builder.ConfigureLifecycleEvents(events =>
        {
            events.AddWindows(windows =>
            {
                windows.OnWindowCreated(window =>
                {
                    try
                    {
                        WindowsWindowChrome.ApplyMainWindowChrome(window);
                    }
                    catch (Exception ex)
                    {
                        WindowsEventLogger.Error("MauiProgram", "OnWindowCreated chrome failed; using default chrome", ex);
                    }
                });
                windows.OnActivated((window, _) =>
                {
                    try
                    {
                        WindowsWindowChrome.ApplyMainWindowChrome(window);
                    }
                    catch (Exception ex)
                    {
                        WindowsEventLogger.Error("MauiProgram", "OnActivated chrome failed; using default chrome", ex);
                    }
                });
            });
        });

        WindowHandler.Mapper.ModifyMapping(nameof(IWindow.Content), (handler, view, action) =>
        {
            try
            {
                if (handler.PlatformView is WinUiWindow nativeWindow)
                {
                    WindowsWindowChrome.PrepareBeforeMauiConnect(nativeWindow);
                }
            }
            catch (Exception ex)
            {
                WindowsEventLogger.Error("MauiProgram", "Pre-connect chrome failed; using default chrome", ex);
            }

            action?.Invoke(handler, view);

            try
            {
                if (handler.PlatformView is WinUiWindow connectedWindow)
                {
                    WindowsWindowChrome.ApplyMainWindowChrome(connectedWindow, handler.MauiContext);
                }
            }
            catch (Exception ex)
            {
                WindowsEventLogger.Error("MauiProgram", "Post-connect chrome failed; using default chrome", ex);
            }
        });

        WindowHandler.Mapper.ModifyMapping(nameof(IWindow.Title), (handler, view, action) =>
        {
            try
            {
                if (handler.PlatformView is WinUiWindow nativeWindow)
                {
                    WindowsWindowChrome.ApplyMainWindowChrome(nativeWindow, handler.MauiContext);
                }
            }
            catch (Exception ex)
            {
                WindowsEventLogger.Error("MauiProgram", "Title-mapping chrome failed; using default chrome", ex);
            }
        });

        WindowHandler.Mapper.ModifyMapping(nameof(IWindow.TitleBar), (handler, view, action) =>
        {
            action?.Invoke(handler, view);

            try
            {
                if (handler.PlatformView is WinUiWindow nativeWindow)
                {
                    WindowsWindowChrome.ApplyMainWindowChrome(nativeWindow, handler.MauiContext);
                }
            }
            catch (Exception ex)
            {
                WindowsEventLogger.Error("MauiProgram", "TitleBar-mapping chrome failed; using default chrome", ex);
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

#if ANDROID
        builder.Services.AddSingleton<INativeVideoPlayerService, AndroidVideoPlayerService>();
#elif WINDOWS
            builder.Services.AddSingleton<INativeVideoPlayerService, Platforms.Windows.WindowsVideoPlayerService>();
            builder.Services.AddSingleton<IOverlayCloseService, VardyParty.Platforms.Windows.OverlayCloseService>();
#elif IOS
            builder.Services.AddSingleton<INativeVideoPlayerService, IosVideoPlayerService>();
#elif MACCATALYST
            builder.Services.AddSingleton<INativeVideoPlayerService, MacCatalystVideoPlayerService>();
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

        // UI sounds: registered per composition root (never in AddVardyParty).
        // Must precede AddVardyPartyHomeUi so its Null/in-memory TryAdd
        // fallbacks defer to these. Initialised on a background task after
        // first render (HomeHostPage.OnAppearing), never in the startup path.
        // VARDYPARTY_NO_SOUND=1 (or the no-sound flag file, which also reaches
        // packaged MSIX launches) swaps in the null player for crash bisecting.
#if ANDROID || WINDOWS
        builder.Services.AddSingleton<VardyParty.Ports.ISoundPreferencesStore, MauiSoundPreferencesStore>();
        builder.Services.AddSingleton<VardyParty.Ports.IDnsPreferencesStore, MauiDnsPreferencesStore>();
        if (VardyParty.Ports.UiSoundKillSwitch.IsDisabled)
        {
            Console.WriteLine($"[MauiProgram] UI sounds disabled via {VardyParty.Ports.UiSoundKillSwitch.Trigger} (NullUiSoundPlayer)");
#if WINDOWS
            WindowsEventLogger.Info("MauiProgram", $"UI sounds disabled via {VardyParty.Ports.UiSoundKillSwitch.Trigger} (NullUiSoundPlayer)");
#endif
            builder.Services.AddSingleton<VardyParty.Ports.IUiSoundPlayer, VardyParty.Ports.NullUiSoundPlayer>();
        }
        else
        {
#if ANDROID
            builder.Services.AddSingleton<VardyParty.Ports.IUiSoundPlayer, AndroidUiSoundPlayer>();
#elif WINDOWS
            builder.Services.AddSingleton<VardyParty.Ports.IUiSoundPlayer, WindowsUiSoundPlayer>();
#endif
        }
#endif

        // Shared XAML homepage (the Blazor UI was removed on this branch; every
        // platform boots HomeHostPage).
        builder.Services.AddSingleton<VardyParty.HomeUi.IHomeAssetLocator, MauiHomeAssetLocator>();
        builder.Services.AddVardyPartyHomeUi();
#if WINDOWS
        if (IsWindowsPackaged)
        {
            builder.Services.AddSingleton<IRunningAppVersion>(_ =>
                new AssemblyRunningAppVersion(typeof(MauiProgram).Assembly));
            builder.Services.AddSingleton<IDesktopPendingUpdateStore, FileDesktopPendingUpdateStore>();
            builder.Services.AddSingleton<IDesktopPackageApplier, MsixPackageManagerApplier>();
            builder.Services.AddSingleton<IDesktopUpdateService, GitHubDesktopUpdateService>();
        }
#endif
        builder.Services.AddSingleton<HomeHostPage>();
        builder.Services.AddSingleton(DeviceInfo.Current);

        builder.Services.AddSingleton<Auth0AuthService>();
        builder.Services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<Auth0AuthService>());
        builder.Services.AddSingleton<IAuthLoginService>(sp => sp.GetRequiredService<Auth0AuthService>());
        builder.Services.AddVardyPartyHttpClients(AllowIgnoreSslCertificateErrors(apiSettings));

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