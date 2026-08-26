using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;

namespace VardyParty.Hosting;

public static class ServiceCollectionExtensions
{
    extension(IConfigurationBuilder configuration)
    {
        public IConfigurationBuilder AddSecrets(Assembly secretsAssembly)
        {
            // Secrets are strictly optional: on devices (Android/iOS) the embedded
            // appsettings.json is the real configuration and no user-secrets file
            // exists. A file-based source without an explicit absolute base path
            // makes ConfigurationBuilder.Build() throw on CoreCLR Android (the
            // default base path is not absolute there), so every file probe below
            // uses an explicit absolute path — and the whole thing is guarded so a
            // missing/unresolvable secrets source can never crash startup.
            try
            {
                var basePath = AppContext.BaseDirectory;
                if (string.IsNullOrWhiteSpace(basePath) || !Path.IsPathFullyQualified(basePath))
                    return configuration;

                var appSettingsPath = Path.Combine(basePath, "appsettings.json");
                if (!File.Exists(appSettingsPath))
                    return configuration;

                // Build temp config to check AllowUserSecrets flag
                var tempConfig = new ConfigurationBuilder()
                    .AddJsonFile(appSettingsPath, true)
                    .Build();

                // Default to false for security (only allow if explicitly set to true in local appsettings.json)
                var allowUserSecrets = tempConfig.GetValue("AllowUserSecrets", false);

                // Only load user secrets if allowed AND secrets file exists
                if (allowUserSecrets && TryGetUserSecretsPath(secretsAssembly, out var secretsPath) && File.Exists(secretsPath))
                    return configuration.AddUserSecrets(secretsAssembly, false);
            }
            catch
            {
                // An unreadable optional secrets source must never take down startup.
            }

            return configuration;
        }
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection BindConfiguration<T>(string configSection)
            where T : class
        {
            services.AddOptions<T>()
                .Configure<IConfiguration>((settings, configuration) =>
                {
                    var section = configuration.GetSection(configSection);
                    if (section.Exists()) section.Bind(settings);
                    // If section doesn't exist, ConfigurationValidator will catch it at startup
                });

            return services;
        }
    }

    private static bool TryGetUserSecretsPath(Assembly assembly, out string? path)
    {
        try
        {
            var userSecretsId = assembly.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
            if (userSecretsId == null)
            {
                path = null;
                return false;
            }

            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
            return true;
        }
        catch
        {
            path = null;
            return false;
        }
    }
}