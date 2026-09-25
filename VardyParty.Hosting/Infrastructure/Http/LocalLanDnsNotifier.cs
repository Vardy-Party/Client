using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Hosting;

/// <summary>
/// Name of the typed LAN play client. <see cref="LocalLanDnsNotifier"/> borrows
/// that pipeline per POST without holding the typed client.
/// </summary>
public static class LocalLanPlayHttpClient
{
    public const string Name = "ILocalLanPlayService";
}

/// <summary>
/// Tells the discovered resolver to restart its browser when DoH changes.
/// Each call resolves a short-lived play client for discovery, then posts with
/// a factory client. The typed <see cref="HttpClient"/> is not stored here.
/// </summary>
public sealed class LocalLanDnsNotifier(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClientFactory,
    ILogger<LocalLanDnsNotifier> logger,
    IDnsPreferencesStore? dnsPreferences = null,
    IDnsOverHttpsEndpoint? dnsEndpoint = null) : ILocalLanDnsNotifier
{
    public async Task NotifyAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var play = scope.ServiceProvider.GetRequiredService<ILocalLanPlayService>();
        var baseUrl = await play.GetServiceBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogInformation(
                "[LocalLanPlay] DoH preference is now {Enabled}, but no local service was discovered to restart",
                enabled);
            return;
        }

        string? resolver = null;
        if (enabled)
        {
            resolver = dnsEndpoint?.Address?.ToString();
            if (string.IsNullOrWhiteSpace(resolver))
            {
                logger.LogWarning(
                    "[LocalLanPlay] DoH is on but no resolver address is configured; not restarting the local browser");
                return;
            }
        }

        var url = $"{baseUrl.TrimEnd('/')}/dns";
        try
        {
            var payload = JsonSerializer.Serialize(new { enabled, resolver });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            ApplyDohHeaders(request);
            var http = httpClientFactory.CreateClient(LocalLanPlayHttpClient.Name);
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "[LocalLanPlay] POST /dns returned {StatusCode} (enabled={Enabled}, resolver={Resolver})",
                    (int)response.StatusCode,
                    enabled,
                    resolver ?? "(system DNS)");
                return;
            }

            logger.LogInformation(
                "[LocalLanPlay] Told local service to use {Doh}",
                enabled ? resolver : "system DNS");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanPlay] Failed to notify local service of DoH preference");
        }
    }

    private void ApplyDohHeaders(HttpRequestMessage request)
    {
        if (dnsPreferences is null)
            return;

        var enabled = dnsPreferences.LoadDnsOverHttpsFallbackEnabled();
        request.Headers.TryAddWithoutValidation("X-Vardy-Doh", enabled ? "1" : "0");
        if (!enabled)
            return;

        var resolver = dnsEndpoint?.Address?.ToString();
        if (!string.IsNullOrWhiteSpace(resolver))
            request.Headers.TryAddWithoutValidation("X-Vardy-Doh-Resolver", resolver);
    }
}
