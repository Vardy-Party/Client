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
    private readonly SemaphoreSlim _hostSessionGate = new(1, 1);
    private int _shareGeneration;
    private int _inviteWatch;
    private int _localServiceWatch;
    private string _status = "";
    private bool _offersRemoteCompute = true;
    private bool _hasRelayUser;
    private bool _hostSessionLive;

    private const string ApiHasNoRemoteCompute = "This API does not offer remote compute";

    internal const string PairedStatus = "Paired. This device uses that computer to find streams.";

    internal const string StoppedGuestStatus = "Stopped. This device finds streams on its own.";

    internal const string InviteUsedStatus = "That invite code has been used.";

    internal const string CheckingInviteStatus = "Checking that invite code...";

    public bool RedeemBusy { get; private set; }

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

    public string InviteCode => CurrentInvite();

    public string Status => _status;

    public event Action? Changed;

    public async Task SetShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            Interlocked.Increment(ref _shareGeneration);
            StopInviteWatch();
            StopLocalServiceWatch();
            await _hostSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopHostAsync(cancellationToken).ConfigureAwait(false);
                _hostSessionLive = false;
                AbandonShare();
                SetStatus("Sharing stopped.");
            }
            finally
            {
                _hostSessionGate.Release();
            }

            return;
        }

        await _hostSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartHostSessionAsync(
                    abandonOnFailure: true,
                    Volatile.Read(ref _shareGeneration),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _hostSessionGate.Release();
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
            SetRedeemBusy(true);
            SetStatus(CheckingInviteStatus);
            try
            {
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
                _hostSessionLive = false;
                AbandonShare();
            }

            preferences.SavePairedHostSub(paired.HostSub);
            if (IsGuestPaired)
            {
                SetStatus(PairedStatus);
            }
            }
            finally
            {
                SetRedeemBusy(false);
            }
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
        SetStatus(StoppedGuestStatus);
        return Task.CompletedTask;
    }

    public async Task RefreshAccessAsync(CancellationToken cancellationToken = default)
    {
        var token = tokens is null
            ? null
            : await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var rolesClaim = authSettings?.Value.RequiredRoleClaimType;
        _hasRelayUser = AuthAccessTokenRoles.HasRelayUser(token, rolesClaim);
        if (preferences.LoadInviteCode().Length > 0 && InviteCode.Length == 0)
        {
            SetStatus("That invite code is no longer valid.");
        }
        else if (IsGuestPaired && !ShareEnabled)
        {
            SetStatus(PairedStatus);
        }

        Changed?.Invoke();

        if (CanHostOnThisDevice() && HasRelayUser && ShareEnabled && !_hostSessionLive)
            await ResumeHostSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool CanHostOnThisDevice() =>
        !OperatingSystem.IsAndroid()
        && !OperatingSystem.IsIOS()
        && !OperatingSystem.IsMacCatalyst();

    private async Task ResumeHostSessionAsync(CancellationToken cancellationToken)
    {
        await _hostSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hostSessionLive)
                return;

            StopInviteWatch();
            preferences.SaveInviteCode("");
            preferences.SaveInviteExpiresAt(0);
            SetStatus("Connecting your local-service...");
            await StartHostSessionAsync(
                    abandonOnFailure: false,
                    Volatile.Read(ref _shareGeneration),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _hostSessionGate.Release();
        }
    }

    private async Task StartHostSessionAsync(bool abandonOnFailure, int generation, CancellationToken cancellationToken)
    {
        string? correlationId = null;
        try
        {
            if (ShareRequestCancelled(generation))
            {
                return;
            }

            var api = apiSettings.Value.HeadlessBaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(api))
            {
                FailStart("This device", "API address is not configured");
                return;
            }

            correlationId = Guid.NewGuid().ToString("N");
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{api}/compute/pairs")
            {
                Content = new StringContent("")
            };
            createRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
            using var create = await http.SendAsync(createRequest, cancellationToken).ConfigureAwait(false);
            if (ShareRequestCancelled(generation))
            {
                return;
            }

            if (create.StatusCode == HttpStatusCode.NotFound)
            {
                NoteMissingRoute();
                FailStart("Relay", ApiHasNoRemoteCompute, correlationId);
                logger.LogWarning("[RemoteCompute] {CorrelationId} pair create returned 404", correlationId);
                return;
            }

            if (create.StatusCode == HttpStatusCode.Forbidden)
            {
                FailStart("Relay", "This account needs the relay-user role", correlationId);
                return;
            }

            if (!create.IsSuccessStatusCode)
            {
                FailStart("Relay", "Could not create an invite code", correlationId);
                logger.LogWarning("[RemoteCompute] {CorrelationId} pair create returned {Status}", correlationId, (int)create.StatusCode);
                return;
            }

            var created = await create.Content.ReadFromJsonAsync<PairCreated>(cancellationToken).ConfigureAwait(false);
            if (ShareRequestCancelled(generation))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(created?.Code))
            {
                FailStart("Relay", "Could not create an invite code", correlationId);
                return;
            }

            var local = await lan.GetServiceBaseUrlAsync(cancellationToken).ConfigureAwait(false);
            if (ShareRequestCancelled(generation))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(local))
            {
                FailStart("Local service", "No local service on this network", correlationId);
                return;
            }

            using var start = new HttpRequestMessage(HttpMethod.Post, $"{local.TrimEnd('/')}/compute/host/start")
            {
                Content = JsonContent.Create(new { apiBaseUrl = api, correlationId })
            };
            using var started = await http.SendAsync(start, cancellationToken).ConfigureAwait(false);
            if (ShareRequestCancelled(generation))
            {
                return;
            }

            if (started.StatusCode == HttpStatusCode.Forbidden)
            {
                FailStart("Relay", "This account needs the relay-user role", correlationId);
                return;
            }

            if (!started.IsSuccessStatusCode)
            {
                FailStart("Local service", "The local service did not start sharing", correlationId);
                logger.LogWarning("[RemoteCompute] {CorrelationId} host start returned {Status}", correlationId, (int)started.StatusCode);
                return;
            }

            StopLocalServiceWatch();
            _hostSessionLive = true;
            preferences.SavePairedHostSub("");
            preferences.SaveInviteExpiresAt(created.ExpiresAt);
            preferences.SaveInviteCode(created.Code);
            preferences.SaveShareEnabled(true);
            SetStatus($"Sharing your local-service. Give this code to the other device. ({correlationId})");
            if (!string.IsNullOrWhiteSpace(local))
            {
                StartInviteWatch(local);
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            if (ShareRequestCancelled(generation))
            {
                return;
            }

            logger.LogWarning(ex, "[RemoteCompute] {CorrelationId} share failed", correlationId);
            FailStart("Relay", "Could not reach the compute relay", correlationId);
        }

        void FailStart(string component, string message, string? id = null)
        {
            _hostSessionLive = false;
            if (abandonOnFailure)
                AbandonShare();
            else if (message.Contains("No local service", StringComparison.Ordinal))
                StartLocalServiceWatch();
            Fail(component, message, id);
        }
    }

    private bool ShareRequestCancelled(int generation) =>
        Volatile.Read(ref _shareGeneration) != generation;

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
        StopInviteWatch();
        preferences.SaveShareEnabled(false);
        preferences.SaveInviteCode("");
        preferences.SaveInviteExpiresAt(0);
    }

    private string CurrentInvite()
    {
        var code = preferences.LoadInviteCode();
        if (code.Length == 0)
        {
            return "";
        }

        var expiresAt = preferences.LoadInviteExpiresAt();
        if (expiresAt > 0 && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            preferences.SaveInviteCode("");
            preferences.SaveInviteExpiresAt(0);
            return "";
        }

        return code;
    }

    private void StartInviteWatch(string localBase)
    {
        var generation = Interlocked.Increment(ref _inviteWatch);
        _ = WatchInviteAsync(localBase, generation);
    }

    private void StopInviteWatch() => Interlocked.Increment(ref _inviteWatch);

    private void StartLocalServiceWatch()
    {
        var generation = Interlocked.Increment(ref _localServiceWatch);
        _ = WatchLocalServiceAsync(generation);
    }

    private void StopLocalServiceWatch() => Interlocked.Increment(ref _localServiceWatch);

    private async Task WatchLocalServiceAsync(int generation)
    {
        while (generation == Volatile.Read(ref _localServiceWatch) && ShareEnabled && !_hostSessionLive)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (generation != Volatile.Read(ref _localServiceWatch) || !ShareEnabled || _hostSessionLive)
            {
                return;
            }

            try
            {
                var local = await lan.GetServiceBaseUrlAsync(CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(local))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug(ex, "[RemoteCompute] Local service check failed");
                continue;
            }

            await ResumeHostSessionAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Polls the local-service while this device shares. Clears a redeemed
    /// invite, and re-runs the host start when the local-service restarts
    /// (its relay socket is gone, and status says <c>sharing: false</c>).
    /// </summary>
    private async Task WatchInviteAsync(string localBase, int generation)
    {
        var misses = 0;
        while (generation == Volatile.Read(ref _inviteWatch) && ShareEnabled && _hostSessionLive)
        {
            var reachable = true;
            HostStatus? status = null;
            try
            {
                using var response = await http.GetAsync(
                    $"{localBase.TrimEnd('/')}/compute/host/status",
                    CancellationToken.None).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _inviteWatch))
                {
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    status = await response.Content.ReadFromJsonAsync<HostStatus>().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogDebug(ex, "[RemoteCompute] Host status check failed");
                reachable = false;
            }

            // One missed poll is a blip; three in a row is a local-service that went away.
            misses = reachable ? 0 : misses + 1;
            switch (HostWatch.Decide(misses < 3, status?.Sharing, status?.InviteUsed == true, InviteCode.Length > 0))
            {
                case HostWatchAction.InviteUsed:
                    preferences.SaveInviteCode("");
                    preferences.SaveInviteExpiresAt(0);
                    SetStatus(InviteUsedStatus);
                    break;
                case HostWatchAction.Restart:
                    logger.LogInformation("[RemoteCompute] Local service is no longer hosting; starting the host session again");
                    _hostSessionLive = false;
                    await ResumeHostSessionAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                case HostWatchAction.Offline:
                    logger.LogInformation("[RemoteCompute] Local service is not answering; waiting for it to come back");
                    _hostSessionLive = false;
                    StartLocalServiceWatch();
                    return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private void Fail(string component, string message, string? correlationId = null) =>
        SetStatus(new RemoteComputeFault(
            component,
            message,
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId).Display);

    private void SetRedeemBusy(bool busy)
    {
        if (RedeemBusy == busy)
        {
            return;
        }

        RedeemBusy = busy;
        Changed?.Invoke();
    }

    private void SetStatus(string status)
    {
        _status = status;
        Changed?.Invoke();
    }

    private sealed class PairCreated
    {
        public string? Code { get; set; }

        public long ExpiresAt { get; set; }
    }

    private sealed class HostStatus
    {
        public bool? Sharing { get; set; }

        public bool InviteUsed { get; set; }
    }

    private sealed class PairRedeemed
    {
        public string? HostSub { get; set; }
    }
}
