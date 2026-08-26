using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VardyParty.Presentation;

namespace VardyParty.HomeUi;

/// <summary>
/// Registers the shared MAUI homepage. Hosts still register
/// <see cref="IHomeAssetLocator"/> (asset paths differ per head) and the
/// catalog services from AddVardyParty().
/// </summary>
public static class HomeUiServiceCollectionExtensions
{
    public static IServiceCollection AddVardyPartyHomeUi(this IServiceCollection services)
    {
        services.TryAddSingleton<MenuViewModel>();
        services.TryAddSingleton<IBadgeImageLoader, SkiaBadgeImageLoader>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<Views.HomePage>();
        return services;
    }
}
