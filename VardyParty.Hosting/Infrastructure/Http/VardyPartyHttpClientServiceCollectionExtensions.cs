using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;

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

        services.TryAddSingleton<IDnsPreferencesStore, InMemoryDnsPreferencesStore>();
        services.AddSingleton<CloudflareDnsOverHttpsClient>();
        services.AddSingleton<IDnsOverHttpsClient>(sp => sp.GetRequiredService<CloudflareDnsOverHttpsClient>());
        services.AddSingleton<IHostNameResolver, SystemThenDohHostNameResolver>();

        SocketsHttpHandler CreateHandler(IServiceProvider sp, bool ignoreSsl = false, bool useCookies = true) =>
            DualStackSocketsHttpHandler.Create(
                ignoreSslCertificateErrors: ignoreSsl,
                useCookies: useCookies,
                hostNameResolver: sp.GetRequiredService<IHostNameResolver>());

        services.AddHttpClient(Auth0HttpClients.Name)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp));

        services.AddHttpClient(PlaybackHttpClients.Probe)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp))
            .ConfigureHttpClient(client => client.Timeout = PlaybackHttpClients.ProbeTimeout);

        services.AddHttpClient<ILocalLanPlayService, LocalLanPlayService>();

        services.AddHttpClient<IBbcFixturesService, BbcFixturesService>()
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp));

        services.AddHttpClient<IStreamHealthService, StreamHealthService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp, ignoreSslCertificateErrors));

        services.AddHttpClient<ApiService>()
            .AddHttpMessageHandler<Auth0ApiTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp, ignoreSslCertificateErrors))
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    VardyPartyClientApiVersion.HeaderName,
                    VardyPartyClientApiVersion.DefaultHeaderValue);
            });
        services.AddTransient<IApiService>(sp => sp.GetRequiredService<ApiService>());
        services.AddTransient<IGamesCatalogApi>(sp => sp.GetRequiredService<ApiService>());
        services.AddTransient<ResolveFreshPlaybackUrlAsync>(sp =>
            PlaybackUrlResolver.Bind(sp.GetRequiredService<IApiService>()));
        services.AddTransient<IAuth0OAuthClient, Auth0OAuthClient>();

        services.AddHttpClient<IStreamHealthChecker, StreamHealthChecker>()
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp));

        // Named client for Android ExoPlayer / managed media fetches (DoH-aware).
        services.AddHttpClient(PlaybackHttpClients.Media)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp))
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddHttpClient("StreamApi")
            .AddHttpMessageHandler<M3U8HttpHandler>()
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp, useCookies: true));

        services.AddHttpClient(GitHubDesktopUpdateService.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VardyParty-Client");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                client.Timeout = TimeSpan.FromSeconds(20);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp));

        services.AddHttpClient(GitHubDesktopUpdateService.AssetHttpClientName, client =>
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VardyParty-Client");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/octet-stream");
                client.Timeout = TimeSpan.FromMinutes(15);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp));

        return services;
    }
}
