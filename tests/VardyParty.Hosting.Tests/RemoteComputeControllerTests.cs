using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Auth;
using VardyParty.Kernel;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Hosting.Tests;

public class RemoteComputeControllerTests
{
    [Fact]
    public async Task Redeem_WhenForbiddenForRelayUser_SetsStatus()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.Forbidden,
            """{"error":"Insufficient permissions. Relay user role required."}"""));
        var sut = Create(handler, lanBase: null);

        await sut.RedeemAsync("ABCD2345");

        Assert.Contains("Relay failed", sut.Status, StringComparison.Ordinal);
        Assert.Contains("relay-user", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_WhenNoLocalService_LeavesTheInviteCodeEmpty()
    {
        // Arrange
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"code":"ABCD2345","expiresAt":1}"""));
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveInviteCode("ABCD2345");
        var sut = Create(handler, lanBase: null, preferences);

        // Act
        await sut.SetShareEnabledAsync(true);

        // Assert
        Assert.False(sut.ShareEnabled);
        Assert.Equal("", sut.InviteCode);
        Assert.Contains("Local service failed", sut.Status, StringComparison.Ordinal);
        Assert.Contains("No local service", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_WhenHostDoesNotStart_LeavesTheInviteCodeEmpty()
    {
        // Arrange
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/compute/host/start", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.InternalServerError, """{"error":"no"}""");
            }

            return Json(HttpStatusCode.OK, """{"code":"ABCD2345","expiresAt":1}""");
        });
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveInviteCode("STALE234");
        var sut = Create(handler, lanBase: "http://127.0.0.1:9", preferences);

        // Act
        await sut.SetShareEnabledAsync(true);

        // Assert
        Assert.False(sut.ShareEnabled);
        Assert.Equal("", sut.InviteCode);
        Assert.Contains("did not start sharing", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_WhenTheRelayCannotBeReached_SetsStatusAndClearsTheInviteCode()
    {
        // Arrange
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("down"));
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveInviteCode("STALE234");
        var sut = Create(handler, lanBase: null, preferences);

        // Act
        await sut.SetShareEnabledAsync(true);

        // Assert
        Assert.False(sut.ShareEnabled);
        Assert.Equal("", sut.InviteCode);
        Assert.Contains("Relay failed", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Could not reach the compute relay", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeem_WhenTheRelayTimesOut_SetsStatus()
    {
        // Arrange
        var handler = new ScriptedHandler(_ => throw new TaskCanceledException("timed out"));
        var sut = Create(handler, lanBase: null);

        // Act
        await sut.RedeemAsync("ABCD2345");

        // Assert
        Assert.Contains("Relay failed", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Could not redeem that invite code", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_WhenTheRouteIsMissing_SaysTheApiDoesNotOfferRemoteCompute()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.NotFound, """{"error":"not found"}"""));
        var sut = Create(handler, lanBase: null);

        await sut.SetShareEnabledAsync(true);

        Assert.False(sut.ShareEnabled);
        Assert.False(sut.OffersRemoteCompute);
        Assert.Contains("does not offer remote compute", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeem_WhenTheRouteIsMissing_SaysTheApiDoesNotOfferRemoteCompute()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.NotFound, """{"error":"not found"}"""));
        var sut = Create(handler, lanBase: null);

        await sut.RedeemAsync("ABCD2345");

        Assert.False(sut.IsGuestPaired);
        Assert.False(sut.OffersRemoteCompute);
        Assert.Contains("does not offer remote compute", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_WhenTheTokenHasRelayUser_AllowsTheMenu()
    {
        var sut = Create(
            new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}")),
            lanBase: null,
            tokens: new StaticTokens(Jwt("""{"https://jonbreen.uk/roles":["stream-viewer","relay-user"]}""")));

        await sut.RefreshAccessAsync();

        Assert.True(sut.HasRelayUser);
    }

    [Fact]
    public async Task Refresh_WhenTheTokenLacksRelayUser_HidesTheMenu()
    {
        var sut = Create(
            new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}")),
            lanBase: null,
            tokens: new StaticTokens(Jwt("""{"https://jonbreen.uk/roles":["stream-viewer"]}""")));

        await sut.RefreshAccessAsync();

        Assert.False(sut.HasRelayUser);
    }

    [Fact]
    public async Task Refresh_WhenSharingWithRelayUser_ResumesHostAndReplacesTheInvite()
    {
        var paths = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            paths.Add(path);
            if (path.EndsWith("/compute/host/start", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"ok":true}""");

            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":9999999999999}""");
        });
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveShareEnabled(true);
        preferences.SaveInviteCode("STALE234");
        var sut = Create(
            handler,
            lanBase: "http://127.0.0.1:9",
            preferences,
            tokens: new StaticTokens(Jwt("""{"https://jonbreen.uk/roles":["stream-viewer","relay-user"]}""")));

        await sut.RefreshAccessAsync();

        Assert.Contains(paths, path => path.EndsWith("/compute/pairs", StringComparison.Ordinal));
        Assert.Contains(paths, path => path.EndsWith("/compute/host/start", StringComparison.Ordinal));
        Assert.True(sut.ShareEnabled);
        Assert.Equal("HOST2345", sut.InviteCode);
        Assert.Contains("Sharing your local-service", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_WhenShareIsOff_DoesNotPostPairs()
    {
        var paths = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            paths.Add(request.RequestUri?.AbsolutePath ?? "");
            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":1}""");
        });
        var sut = Create(
            handler,
            lanBase: "http://127.0.0.1:9",
            tokens: new StaticTokens(Jwt("""{"https://jonbreen.uk/roles":["relay-user"]}""")));

        await sut.RefreshAccessAsync();

        Assert.DoesNotContain(paths, path => path.EndsWith("/compute/pairs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_WhenOnlyStreamViewer_DoesNotPostPairsAndLeavesTheStaleInvite()
    {
        var paths = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            paths.Add(request.RequestUri?.AbsolutePath ?? "");
            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":1}""");
        });
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveShareEnabled(true);
        preferences.SaveInviteCode("STALE234");
        var sut = Create(
            handler,
            lanBase: "http://127.0.0.1:9",
            preferences,
            tokens: new StaticTokens(Jwt("""{"https://jonbreen.uk/roles":["stream-viewer"]}""")));

        await sut.RefreshAccessAsync();

        Assert.DoesNotContain(paths, path => path.EndsWith("/compute/pairs", StringComparison.Ordinal));
        Assert.Equal("STALE234", sut.InviteCode);
        Assert.True(sut.ShareEnabled);
    }

    [Fact]
    public async Task Refresh_WhenHostStartReturns500_KeepsShareEnabled()
    {
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/compute/host/start", StringComparison.Ordinal))
                return Json(HttpStatusCode.InternalServerError, """{"error":"no"}""");

            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":1}""");
        });
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveShareEnabled(true);
        preferences.SaveInviteCode("STALE234");
        var sut = Create(
            handler,
            lanBase: "http://127.0.0.1:9",
            preferences,
            tokens: new StaticTokens(Jwt("""{"https://jonbreen.uk/roles":["relay-user"]}""")));

        await sut.RefreshAccessAsync();

        Assert.True(sut.ShareEnabled);
        Assert.Contains("did not start sharing", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopUsingRemote_ClearsThePairing()
    {
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SavePairedHostSub("host-1");
        var sut = Create(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}")), lanBase: null, preferences);

        await sut.StopUsingRemoteAsync();

        Assert.False(sut.IsGuestPaired);
        Assert.Contains(RemoteComputeController.StoppedGuestStatus, sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_WhenAlreadyAGuest_ClearsThePairing()
    {
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SavePairedHostSub("other-host");
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/compute/host/start", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            }

            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":9999999999999}""");
        });
        var sut = Create(handler, lanBase: "http://127.0.0.1:9", preferences);

        await sut.SetShareEnabledAsync(true);

        Assert.True(sut.ShareEnabled);
        Assert.False(sut.IsGuestPaired);
        Assert.Equal("HOST2345", sut.InviteCode);
    }

    [Fact]
    public async Task Refresh_WhenLocalServiceAppearsLater_ClearsTheFailure()
    {
        var calls = 0;
        var lan = new Mock<ILocalLanPlayService>();
        lan.Setup(service => service.GetServiceBaseUrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref calls) == 1 ? null : "http://127.0.0.1:9");
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/compute/host/status", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"sharing":true,"inviteUsed":false}""");
            }

            if (path.EndsWith("/compute/host/start", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            }

            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":9999999999999}""");
        });
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveShareEnabled(true);
        var sut = Create(
            handler,
            lanBase: null,
            preferences,
            tokens: new StaticTokens(Jwt("""{"https://jonbreen.uk/roles":["relay-user"]}""")),
            lan: lan.Object);

        await sut.RefreshAccessAsync();

        Assert.Contains("No local service", sut.Status, StringComparison.Ordinal);

        for (var attempt = 0; attempt < 50 && sut.Status.Contains("No local service", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(100);
        }

        Assert.Contains("Sharing your local-service", sut.Status, StringComparison.Ordinal);
        Assert.Equal("HOST2345", sut.InviteCode);
        Assert.True(sut.ShareEnabled);
    }

    [Fact]
    public async Task Share_WhenTheInviteIsUsed_StopsDisplayingTheCode()
    {
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/compute/host/status", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"sharing":true,"inviteUsed":true}""");
            }

            if (path.EndsWith("/compute/host/start", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            }

            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":9999999999999}""");
        });
        var sut = Create(handler, lanBase: "http://127.0.0.1:9");

        await sut.SetShareEnabledAsync(true);

        for (var attempt = 0; attempt < 20 && sut.InviteCode.Length > 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal("", sut.InviteCode);
        Assert.Contains(RemoteComputeController.InviteUsedStatus, sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void InviteCode_WhenExpired_IsNotDisplayed()
    {
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveInviteCode("STALE234");
        preferences.SaveInviteExpiresAt(1);
        var sut = Create(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}")), lanBase: null, preferences);

        Assert.Equal("", sut.InviteCode);
    }

    [Fact]
    public async Task Refresh_WhenAlreadyPaired_SaysWhoseLocalServiceIsInUse()
    {
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SavePairedHostSub("host-1");
        var sut = Create(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}")), lanBase: null, preferences);

        await sut.RefreshAccessAsync();

        Assert.Contains(RemoteComputeController.PairedStatus, sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeem_WhileTheRelayIsChecking_SaysItIsChecking()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(gate.Task, Json(HttpStatusCode.OK, """{"hostSub":"host-1"}"""));
        var sut = Create(handler, lanBase: null);

        var redeem = sut.RedeemAsync("GUEST234");
        for (var attempt = 0; attempt < 50 && !sut.Status.Contains("Checking", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(RemoteComputeController.CheckingInviteStatus, sut.Status, StringComparison.Ordinal);
        Assert.True(sut.RedeemBusy);
        gate.SetResult();
        await redeem;
        Assert.False(sut.RedeemBusy);
        Assert.Contains(RemoteComputeController.PairedStatus, sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeem_WhenAlreadySharing_ClearsTheHostInvite()
    {
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SaveShareEnabled(true);
        preferences.SaveInviteCode("HOST2345");
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/compute/host/stop", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            }

            return Json(HttpStatusCode.OK, """{"hostSub":"host-1"}""");
        });
        var sut = Create(handler, lanBase: "http://127.0.0.1:9", preferences);

        await sut.RedeemAsync("GUEST234");

        Assert.False(sut.ShareEnabled);
        Assert.Equal("", sut.InviteCode);
        Assert.True(sut.IsGuestPaired);
    }

    [Fact]
    public async Task Share_WhenTurnedOffMidStart_LeavesTheInviteCodeEmpty()
    {
        var handler = new ArriveThenGateHandler(Json(HttpStatusCode.OK, """{"code":"ABCD2345","expiresAt":9999999999999}"""));
        var preferences = new InMemoryRemoteComputePreferences();
        var sut = Create(handler, lanBase: "http://127.0.0.1:9", preferences);

        var start = sut.SetShareEnabledAsync(true);
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = sut.SetShareEnabledAsync(false);
        handler.Release.SetResult();
        await start;
        await stop;

        Assert.False(sut.ShareEnabled);
        Assert.Equal("", sut.InviteCode);
        Assert.Contains("Sharing stopped", sut.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_WhenTheCallerCancels_DoesNotSetStatus()
    {
        // Arrange
        var handler = new ScriptedHandler(_ => throw new TaskCanceledException());
        var sut = Create(handler, lanBase: null);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var thrown = await Record.ExceptionAsync(() => sut.SetShareEnabledAsync(true, cancelled.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal("", sut.Status);
    }

    [Fact]
    public void RelayWire_RoundTripsAChunk()
    {
        var encoded = RelayWire.Encode(7, "ping"u8.ToArray());
        Assert.True(RelayWire.TryDecode(encoded, out var id, out var payload));
        Assert.Equal(7, id);
        Assert.Equal("ping", Encoding.ASCII.GetString(payload));
    }

    private static RemoteComputeController Create(
        HttpMessageHandler handler,
        string? lanBase,
        IRemoteComputePreferences? preferences = null,
        IAuthTokenProvider? tokens = null,
        ILocalLanPlayService? lan = null)
    {
        if (lan is null)
        {
            var mock = new Mock<ILocalLanPlayService>();
            mock.Setup(service => service.GetServiceBaseUrlAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(lanBase);
            lan = mock.Object;
        }
        return new RemoteComputeController(
            new HttpClient(handler),
            Options.Create(new APISettings { HeadlessBaseUrl = "https://api.test" }),
            lan,
            preferences ?? new InMemoryRemoteComputePreferences(),
            NullLogger<RemoteComputeController>.Instance,
            remotePlay: null,
            tokens: tokens,
            authSettings: Options.Create(new Auth0Settings
            {
                Domain = "tenant.example",
                ClientId = "client",
                Audience = "https://api.test",
                Scope = "openid",
                CallbackScheme = "vardyparty",
                RedirectUri = "vardyparty://callback",
                PostLogoutRedirectUri = "vardyparty://callback",
                TokenLeewaySeconds = 60,
                RequiredRoleClaimType = "https://jonbreen.uk/roles",
                RequiredRole = "stream-viewer"
            }));
    }

    private static string Jwt(string payloadJson)
    {
        static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        return Encode("""{"alg":"none"}""") + "." + Encode(payloadJson) + ".x";
    }

    private sealed class StaticTokens(string? accessToken) : IAuthTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false) =>
            Task.FromResult(accessToken);

        public Task<bool> IsAuthenticatedAsync() => Task.FromResult(accessToken is not null);

        public Task LogoutAsync() => Task.CompletedTask;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class GatedHandler(Task gate, HttpResponseMessage response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return response;
        }
    }

    private sealed class ArriveThenGateHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Arrived.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return response;
        }
    }
}
