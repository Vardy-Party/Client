using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace VardyParty.Exceptions;

public static class ServiceCollectionExtensions
{
    extension(IConfigurationBuilder configuration)
    {
        public IConfigurationBuilder AddSecrets(Assembly secretsAssembly)
        {
            var environment = Environment.GetEnvironmentVariable("NETCORE_ENVIRONMENT");

            var isDevelopment = environment != null && environment.ToLower() == "development";

            if (isDevelopment) return configuration.AddUserSecrets(secretsAssembly, false);

            return configuration;
        }
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection BindConfiguration<T>(string configSection)
            where T : class
        {
            services.AddOptions<T>().Configure<IConfiguration>((settings, configuration) =>
            {
                configuration.GetSection(configSection).Bind(settings);
            });
            return services;
        }
    }
}