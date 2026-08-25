using Microsoft.Extensions.DependencyInjection;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Orchestrators;
using VardyParty.Parsers;
using VardyParty.Providers;
using VardyParty.Resolvers;
using VardyParty.Services;

namespace VardyParty.Hosting;

/// <summary>
/// Shared domain registrations. Hosts still add auth, player, prefs store, and HttpClients.
/// </summary>
public static class VardyPartyServiceCollectionExtensions
{
    public static IServiceCollection AddVardyPartyCore(this IServiceCollection services)
    {
        services.AddSingleton<IGameMatcher, GameMatcher>();
        services.AddSingleton<IBbcJsonParser, BbcJsonParser>();
        services.AddSingleton<IBbcHtmlParser, BbcHtmlParser>();
        services.AddSingleton<IStreamDeduplicator, StreamDeduplicator>();
        services.AddSingleton<IGamesCatalogApi>(sp => sp.GetRequiredService<IApiService>());
        services.AddSingleton<IEnrichedGameService, EnrichedGameService>();
        services.AddSingleton<ILeagueFilterService, LeagueFilterService>();
        services.AddSingleton<IHomePagePresentationService, HomePagePresentationService>();
        services.AddSingleton<IStreamSwitchingService, StreamSwitchingService>();
        services.AddSingleton<IStreamSelectionCoordinator, StreamSelectionCoordinator>();
        services.AddSingleton<IStreamResolutionOrchestrator, StreamResolutionOrchestrator>();
        services.AddSingleton<IStreamHealthReporter, StreamHealthReporter>();
        services.AddSingleton<ISessionIdProvider, SessionIdProvider>();
        services.AddSingleton<SelectionState>();
        services.AddSingleton<ILocalLanServiceAvailabilityMonitor, LocalLanServiceAvailabilityMonitor>();
        services.AddSingleton<IStreamResolver, StreamResolver>();
        return services;
    }
}
