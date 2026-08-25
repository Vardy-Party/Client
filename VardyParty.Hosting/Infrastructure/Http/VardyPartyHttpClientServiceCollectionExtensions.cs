using Microsoft.Extensions.DependencyInjection;
using VardyParty.Auth;
using VardyParty.Health;

namespace VardyParty.Hosting;

/// <summary>
/// Typed HttpClients. Internet calls share <see cref="DualStackSocketsHttpHandler"/>;
/// LAN discovery keeps the platform default handler.
/// </summary>
public static class VardyPartyHttpClientServiceCollectionExtensions
{
    public static IServiceCollection AddVardyPartyHttpClients(
        this IServiceCollection services,
        bool ignoreSslCertificateErrors = false)
    {
        services.AddTransient<Auth0ApiTokenHandler>();
        services.AddTransient<M3U8HttpHandler>();

        services.AddHttpClient(Auth0HttpClients.Name)
            .ConfigurePrimaryHttpMessageHandler(() => DualStackSocketsHttpHandler.Create());

        services.AddHttpClient<ILocalLanPlayService, LocalLanPlayService>();

        services.AddHttpClient<IBbcFixturesService, BbcFixturesService>()
            .ConfigurePrimaryHttpMessageHandler(() => DualStackSocketsHttpHandler.Create());

        services.AddHttpClient<IStreamHealthService, StreamHealthService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => DualStackSocketsHttpHandler.Create(ignoreSslCertificateErrors));

        services.AddHttpClient<ApiService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => DualStackSocketsHttpHandler.Create(ignoreSslCertificateErrors))
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    VardyPartyClientApiVersion.HeaderName,
                    VardyPartyClientApiVersion.DefaultHeaderValue);
            });
        services.AddTransient<IApiService>(sp => sp.GetRequiredService<ApiService>());
        services.AddTransient<IGamesCatalogApi>(sp => sp.GetRequiredService<ApiService>());
        services.AddTransient<IAuth0OAuthClient, Auth0OAuthClient>();

        services.AddHttpClient<IStreamHealthChecker, StreamHealthChecker>()
            .ConfigurePrimaryHttpMessageHandler(() => DualStackSocketsHttpHandler.Create());

        services.AddHttpClient("StreamApi")
            .AddHttpMessageHandler<M3U8HttpHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => DualStackSocketsHttpHandler.Create(useCookies: true));

        return services;
    }
}
