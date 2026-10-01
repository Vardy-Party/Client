using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Hosting;

public sealed class RemoteComputeController : IRemoteComputeController
{
    private readonly HttpClient http;
    private readonly IOptions<APISettings> apiSettings;
    private readonly ILocalLanPlayService lan;
    private readonly IRemoteComputePreferences preferences;
    private readonly ILogger<RemoteComputeController> logger;
    private string _status = "";

    public RemoteComputeController(
        HttpClient http,
        IOptions<APISettings> apiSettings,
        ILocalLanPlayService lan,
        IRemoteComputePreferences preferences,
        ILogger<RemoteComputeController> logger,
        IRemoteComputePlay? remotePlay = null)
    {
        this.http = http;
        this.apiSettings = apiSettings;
        this.lan = lan;
        this.preferences = preferences;
        this.logger = logger;
        if (remotePlay is not null)
        {
            remotePlay.Faulted += fault => SetStatus(fault.Display);
        }
    }

    public bool ShareEnabled => preferences.LoadShareEnabled();

    public string InviteCode => preferences.LoadInviteCode();

    public string Status => _status;

    public event Action? Changed;

    public async Task SetShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            await StopHostAsync(cancellationToken).ConfigureAwait(false);
            preferences.SaveShareEnabled(false);
            SetStatus("Sharing stopped.");
            return;
        }

        var api = apiSettings.Value.HeadlessBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(api))
        {
            preferences.SaveShareEnabled(false);
            Fail("This device", "API address is not configured");
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{api}/compute/pairs")
        {
            Content = new StringContent("")
        };
        createRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        using var create = await http.SendAsync(createRequest, cancellationToken).ConfigureAwait(false);
        if (create.StatusCode == HttpStatusCode.Forbidden)
        {
            preferences.SaveShareEnabled(false);
            Fail("Relay", "This account needs the relay-user role", correlationId);
            return;
        }

        if (!create.IsSuccessStatusCode)
        {
            preferences.SaveShareEnabled(false);
            Fail("Relay", "Could not create an invite code", correlationId);
            logger.LogWarning("[RemoteCompute] {CorrelationId} pair create returned {Status}", correlationId, (int)create.StatusCode);
            return;
        }

        var created = await create.Content.ReadFromJsonAsync<PairCreated>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created?.Code))
        {
            preferences.SaveShareEnabled(false);
            Fail("Relay", "Could not create an invite code", correlationId);
            return;
        }

        preferences.SaveInviteCode(created.Code);
        var local = await lan.GetServiceBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(local))
        {
            preferences.SaveShareEnabled(false);
            Fail("Local service", "No local service on this network", correlationId);
            return;
        }

        using var start = new HttpRequestMessage(HttpMethod.Post, $"{local.TrimEnd('/')}/compute/host/start")
        {
            Content = JsonContent.Create(new { apiBaseUrl = api, correlationId })
        };
        using var started = await http.SendAsync(start, cancellationToken).ConfigureAwait(false);
        if (started.StatusCode == HttpStatusCode.Forbidden)
        {
            preferences.SaveShareEnabled(false);
            Fail("Relay", "This account needs the relay-user role", correlationId);
            return;
        }

        if (!started.IsSuccessStatusCode)
        {
            preferences.SaveShareEnabled(false);
            Fail("Local service", "The local service did not start sharing", correlationId);
            logger.LogWarning("[RemoteCompute] {CorrelationId} host start returned {Status}", correlationId, (int)started.StatusCode);
            return;
        }

        preferences.SaveShareEnabled(true);
        SetStatus($"Sharing. Give this code to the phone. ({correlationId})");
    }

    public async Task RedeemAsync(string code, CancellationToken cancellationToken = default)
    {
        var trimmed = code?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            SetStatus("Enter an invite code.");
            return;
        }

        var api = apiSettings.Value.HeadlessBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(api))
        {
            Fail("This device", "API address is not configured");
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        using var redeem = new HttpRequestMessage(HttpMethod.Post, $"{api}/compute/pairs/redeem")
        {
            Content = JsonContent.Create(new { code = trimmed })
        };
        redeem.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        using var response = await http.SendAsync(redeem, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Fail("Relay",
                text.Contains("relay-user", StringComparison.OrdinalIgnoreCase)
                || text.Contains("relay user", StringComparison.OrdinalIgnoreCase)
                    ? "This account needs the relay-user role"
                    : "Invite code is invalid or expired",
                correlationId);
            logger.LogWarning("[RemoteCompute] {CorrelationId} redeem returned 403", correlationId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            Fail("Relay", "Could not redeem that invite code", correlationId);
            return;
        }

        var paired = await response.Content.ReadFromJsonAsync<PairRedeemed>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(paired?.HostSub))
        {
            Fail("Relay", "Could not redeem that invite code", correlationId);
            return;
        }

        preferences.SavePairedHostSub(paired.HostSub);
        SetStatus("Paired with a remote compute host.");
    }

    private async Task StopHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var local = await lan.GetServiceBaseUrlAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(local))
            {
                return;
            }

            using var response = await http.PostAsync($"{local.TrimEnd('/')}/compute/host/stop", new StringContent(""), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[RemoteCompute] Stop sharing failed");
        }
    }

    private void Fail(string component, string message, string? correlationId = null) =>
        SetStatus(new RemoteComputeFault(
            component,
            message,
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId).Display);

    private void SetStatus(string status)
    {
        _status = status;
        Changed?.Invoke();
    }

    private sealed class PairCreated
    {
        public string? Code { get; set; }
    }

    private sealed class PairRedeemed
    {
        public string? HostSub { get; set; }
    }
}
