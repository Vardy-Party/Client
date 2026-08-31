using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VardyParty.Ports;
using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// Registers the shared MAUI homepage. Hosts still register
/// <see cref="IHomeAssetLocator"/> (asset paths differ per head) and the
/// catalog services from AddVardyParty(). Heads with real audio register
/// their <see cref="IUiSoundPlayer"/>/<see cref="ISoundPreferencesStore"/>
/// BEFORE calling this (the TryAdds below are silent fallbacks).
/// </summary>
public static class HomeUiServiceCollectionExtensions
{
    public static IServiceCollection AddVardyPartyHomeUi(this IServiceCollection services)
    {
        services.TryAddSingleton<MenuViewModel>();
        services.TryAddSingleton<IDesktopUpdateService, NullDesktopUpdateService>();
        services.TryAddSingleton<IBadgeImageLoader, SkiaBadgeImageLoader>();
        services.TryAddSingleton<IUiSoundPlayer, NullUiSoundPlayer>();
        services.TryAddSingleton<ISoundPreferencesStore, InMemorySoundPreferencesStore>();
        services.TryAddSingleton<UiSoundService>();
        services.TryAddSingleton<MatchEventNotificationPolicy>();
        services.TryAddSingleton<MatchEventBus>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<Views.HomePage>();
        return services;
    }
}
