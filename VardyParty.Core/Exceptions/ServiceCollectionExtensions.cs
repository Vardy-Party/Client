using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;

namespace VardyParty.Exceptions;

public static class ServiceCollectionExtensions
{
    public static IConfigurationBuilder AddSecrets(this IConfigurationBuilder configuration, Assembly secretsAssembly)
    {
        // Build temp config to check AllowUserSecrets flag
        var tempConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        
        // Default to false for security (only allow if explicitly set to true in local appsettings.json)
        var allowUserSecrets = tempConfig.GetValue<bool>("AllowUserSecrets", defaultValue: false);
        
        // Only load user secrets if allowed AND secrets file exists
        if (allowUserSecrets && TryGetUserSecretsPath(secretsAssembly, out var secretsPath) && File.Exists(secretsPath))
        {
            return configuration.AddUserSecrets(secretsAssembly, optional: false);
        }

        return configuration;
    }

    public static IServiceCollection BindConfiguration<T>(this IServiceCollection services, string configSection)
        where T : class
    {
        services.AddOptions<T>().Configure<IConfiguration>((settings, configuration) =>
        {
            configuration.GetSection(configSection).Bind(settings);
        });
        return services;
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