using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VardyParty.Configuration;
using VardyParty.Exceptions;
using VardyParty.Health;
using VardyParty.Hosting;
using VardyParty.Resolvers;
using VardyParty.Services;

namespace StreamHealthCheckerTool;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var services = new ServiceCollection();

        services.AddLogging(configure =>
        {
            configure.AddConsole();
            configure.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddHttpClient<IStreamHealthChecker, StreamHealthChecker>()
            .ConfigurePrimaryHttpMessageHandler(() => DualStackSocketsHttpHandler.Create());

        services.AddSingleton<IStreamResolver, StreamResolver>()
            .AddSingleton<IStreamDeduplicator, StreamDeduplicator>();
        services.AddHttpClient<IApiService, ApiService>();


        var serviceProvider = services.BuildServiceProvider();

        services
            .BindConfiguration<APISettings>(APISettings.SectionName)
            .BindConfiguration<GamesApiSettings>(GamesApiSettings.SectionName)
            .BindConfiguration<StreamHealthSettings>(StreamHealthSettings.SectionName)
            .BindConfiguration<Auth0Settings>(Auth0Settings.SectionName)
            .BindConfiguration<BbcFixturesSettings>(BbcFixturesSettings.SectionName)
            .BindConfiguration<ApiTokenSettings>(ApiTokenSettings.SectionName);

        var apiService = serviceProvider.GetRequiredService<IApiService>();
        var checker = serviceProvider.GetRequiredService<IStreamHealthChecker>();
        var pageUrl = args[0].Trim();
        Console.WriteLine($"Resolving M3U8 for: {pageUrl}...");

        try
        {
            // ApiService.GetM3U8UrlAsync expects just the stream path/ID usually if using the full headless flow,
            // but based on context it seems to take what the UI passes which is typically the stream URL/ID.
            // The ApiService constructs: $"{_baseUrl}/play/{Uri.EscapeDataString(streamUrl)}"
            // So we should pass the input as is if it matches what the UI sends.

            var m3u8Response = await apiService.GetM3U8UrlAsync(pageUrl);

            if (m3u8Response != null && !string.IsNullOrEmpty(m3u8Response.Url))
            {
                Console.WriteLine($"Resolved M3U8 URL: {m3u8Response.Url}");
                Console.WriteLine("Checking stream health...");

                var health = await checker.CheckStreamHealthAsync(m3u8Response.Url, pageUrl);

                Console.WriteLine($"Health Status: {health.Status}");
                Console.WriteLine($"Check Duration: {health.CheckDurationMs}ms");
                if (!string.IsNullOrEmpty(health.ErrorMessage)) Console.WriteLine($"Error: {health.ErrorMessage}");
            }
            else
            {
                Console.WriteLine("Failed to resolve M3U8 URL.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}