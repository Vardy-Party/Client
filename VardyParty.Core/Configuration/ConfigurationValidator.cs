using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VardyParty.Configuration;

/// <summary>
/// Validates that required configuration sections exist in appsettings.json.
/// Fails fast with clear error messages if critical configuration is missing.
/// </summary>
public static class ConfigurationValidator
{
    private static readonly string[] RequiredSections =
    {
        APISettings.SectionName,
        GamesApiSettings.SectionName,
        BbcFixturesSettings.SectionName,
        StreamHealthSettings.SectionName,
        Auth0Settings.SectionName
    };

    public static void ValidateConfiguration(IConfiguration configuration, ILogger logger)
    {
        var missingSection = RequiredSections.FirstOrDefault(section => !configuration.GetSection(section).Exists());

        if (missingSection != null)
        {
            var message = $"CRITICAL: Required configuration section '{missingSection}' is missing from appsettings.json. " +
                         $"CD/CD may have failed to merge settings. Expected sections: {string.Join(", ", RequiredSections)}";

            logger.LogCritical(message);
            throw new InvalidOperationException(message);
        }

        logger.LogInformation("Configuration validation passed. All required sections present.");
    }
}
