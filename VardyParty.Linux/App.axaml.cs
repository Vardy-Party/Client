using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VardyParty;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Hosting;
using VardyParty.Linux.Services;
using VardyParty.Playback;
using VardyParty.Streaming;
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
        var apiConfig = new ConfigurationBuilder()
            .SetBasePath(appSettingsDirectory)
            .AddJsonFile(appSettingsFileName, false, true)
            .Build()
            .GetSection("Api");
        // Default to production; set VARDYPARTY_DEBUG_API=local|preview to override.
        var debugApiTarget = Environment.GetEnvironmentVariable("VARDYPARTY_DEBUG_API");
        var debugBaseUrl = debugApiTarget?.Trim().ToLowerInvariant() switch
        {
            "local" => apiConfig["HeadlessBaseUrl-Local"],
            "preview" => apiConfig["HeadlessBaseUrl-Preview"],
            "production" or "prod" => apiConfig["HeadlessBaseUrl"],
            _ => apiConfig["HeadlessBaseUrl"],
        };
        if (!string.IsNullOrWhiteSpace(debugBaseUrl))
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HeadlessBaseUrl"] = debugBaseUrl
            });
            Console.WriteLine($"[App] DEBUG: Using API at {debugBaseUrl} (target={debugApiTarget ?? "production"})");
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

        services.AddSingleton<ILeagueFilterPreferencesStore, InMemoryLeagueFilterPreferencesStore>();
        services.AddVardyParty();
        services.AddSingleton<LinuxAuthService>();
        services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<LinuxAuthService>());
        services.AddSingleton<IAuthLoginService>(sp => sp.GetRequiredService<LinuxAuthService>());
        var apiSettings = configuration.GetSection(APISettings.SectionName).Get<APISettings>();
        services.AddVardyPartyHttpClients(apiSettings?.IgnoreSslCertificateErrors ?? false);

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