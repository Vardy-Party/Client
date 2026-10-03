using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Auth;
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
    private readonly IAuthTokenProvider? tokens;
    private readonly IOptions<Auth0Settings>? authSettings;
    private string _status = "";
    private bool _offersRemoteCompute = true;
    private bool _hasRelayUser;

    private const string ApiHasNoRemoteCompute = "This API does not offer remote compute";

    public RemoteComputeController(
        HttpClient http,
        IOptions<APISettings> apiSettings,
        ILocalLanPlayService lan,
        IRemoteComputePreferences preferences,
        ILogger<RemoteComputeController> logger,
        IRemoteComputePlay? remotePlay = null,
        IAuthTokenProvider? tokens = null,
        IOptions<Auth0Settings>? authSettings = null)
    {
        this.http = http;
        this.apiSettings = apiSettings;
        this.lan = lan;
        this.preferences = preferences;
        this.logger = logger;
        this.tokens = tokens;
        this.authSettings = authSettings;
        if (remotePlay is not null)
        {
            remotePlay.Faulted += fault => SetStatus(fault.Display);
        }
    }

    public bool ShareEnabled => preferences.LoadShareEnabled();

    public bool IsGuestPaired => !string.IsNullOrWhiteSpace(preferences.LoadPairedHostSub());

    public bool OffersRemoteCompute => _offersRemoteCompute;

    public bool HasRelayUser => _hasRelayUser;

    public string InviteCode => preferences.LoadInviteCode();

    public string Status => _status;

    public event Action? Changed;

    public async Task SetShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        string? correlationId = null;
        try
        {
            if (!enabled)
            {
                await StopHostAsync(cancellationToken).ConfigureAwait(false);
                AbandonShare();
                SetStatus("Sharing stopped.");
                return;
            }

            var api = apiSettings.Value.HeadlessBaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(api))
            {
                AbandonShare();
                Fail("This device", "API address is not configured");
                return;
            }

            correlationId = Guid.NewGuid().ToString("N");
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{api}/compute/pairs")
            {
                Content = new StringContent("")
            };
            createRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
            using var create = await http.SendAsync(createRequest, cancellationToken).ConfigureAwait(false);
            if (create.StatusCode == HttpStatusCode.NotFound)
            {
                NoteMissingRoute();
                AbandonShare();
                Fail("Relay", ApiHasNoRemoteCompute, correlationId);
                logger.LogWarning("[RemoteCompute] {CorrelationId} pair create returned 404", correlationId);
                return;
            }

            if (create.StatusCode == HttpStatusCode.Forbidden)
            {
                AbandonShare();
                Fail("Relay", "This account needs the relay-user role", correlationId);
                return;
            }

            if (!create.IsSuccessStatusCode)
            {
                AbandonShare();
                Fail("Relay", "Could not create an invite code", correlationId);
                logger.LogWarning("[RemoteCompute] {CorrelationId} pair create returned {Status}", correlationId, (int)create.StatusCode);
                return;
            }

            var created = await create.Content.ReadFromJsonAsync<PairCreated>(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(created?.Code))
            {
                AbandonShare();
                Fail("Relay", "Could not create an invite code", correlationId);
                return;
            }

            var local = await lan.GetServiceBaseUrlAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(local))
            {
                AbandonShare();
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
                AbandonShare();
                Fail("Relay", "This account needs the relay-user role", correlationId);
                return;
            }

            if (!started.IsSuccessStatusCode)
            {
                AbandonShare();
                Fail("Local service", "The local service did not start sharing", correlationId);
                logger.LogWarning("[RemoteCompute] {CorrelationId} host start returned {Status}", correlationId, (int)started.StatusCode);
                return;
            }

            preferences.SavePairedHostSub("");
            preferences.SaveInviteCode(created.Code);
            preferences.SaveShareEnabled(true);
            SetStatus($"Sharing your local-service. Give this code to the other device. ({correlationId})");
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[RemoteCompute] {CorrelationId} share failed", correlationId);
            AbandonShare();
            Fail("Relay", "Could not reach the compute relay", correlationId);
        }
    }

    public async Task RedeemAsync(string code, CancellationToken cancellationToken = default)
    {
        string? correlationId = null;
        try
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

            correlationId = Guid.NewGuid().ToString("N");
            using var redeem = new HttpRequestMessage(HttpMethod.Post, $"{api}/compute/pairs/redeem")
            {
                Content = JsonContent.Create(new { code = trimmed })
            };
            redeem.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
            using var response = await http.SendAsync(redeem, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                NoteMissingRoute();
                Fail("Relay", ApiHasNoRemoteCompute, correlationId);
                logger.LogWarning("[RemoteCompute] {CorrelationId} redeem returned 404", correlationId);
                return;
            }

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
                logger.LogWarning("[RemoteCompute] {CorrelationId} redeem returned {Status}", correlationId, (int)response.StatusCode);
                return;
            }

            var paired = await response.Content.ReadFromJsonAsync<PairRedeemed>(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(paired?.HostSub))
            {
                Fail("Relay", "Could not redeem that invite code", correlationId);
                return;
            }

            if (ShareEnabled)
            {
                await StopHostAsync(cancellationToken).ConfigureAwait(false);
                AbandonShare();
            }

            preferences.SavePairedHostSub(paired.HostSub);
            SetStatus("Using another user's local-service.");
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[RemoteCompute] {CorrelationId} redeem failed", correlationId);
            Fail("Relay", "Could not redeem that invite code", correlationId);
        }
    }

    public Task StopUsingRemoteAsync(CancellationToken cancellationToken = default)
    {
        preferences.SavePairedHostSub("");
        SetStatus("Stopped using their local-service.");
        return Task.CompletedTask;
    }

    public async Task RefreshAccessAsync(CancellationToken cancellationToken = default)
    {
        var token = tokens is null
            ? null
            : await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var rolesClaim = authSettings?.Value.RequiredRoleClaimType;
        _hasRelayUser = HasRelayClaim(token, rolesClaim);
        Changed?.Invoke();
    }

    private static bool HasRelayClaim(string? token, string? rolesClaim)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (AuthAccessTokenRoles.HasRequiredRole(token, rolesClaim, "relay-user"))
        {
            return true;
        }

        if (AuthAccessTokenRoles.HasRequiredRole(token, "permissions", "relay-user"))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(rolesClaim)
            && rolesClaim.EndsWith("/roles", StringComparison.OrdinalIgnoreCase))
        {
            var permissionsClaim = string.Concat(rolesClaim.AsSpan(0, rolesClaim.Length - "roles".Length), "permissions");
            if (AuthAccessTokenRoles.HasRequiredRole(token, permissionsClaim, "relay-user"))
            {
                return true;
            }
        }

        return false;
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

    private void NoteMissingRoute() => _offersRemoteCompute = false;

    private void AbandonShare()
    {
        preferences.SaveShareEnabled(false);
        preferences.SaveInviteCode("");
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
