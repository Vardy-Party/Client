using System;
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
    public async Task StopUsingRemote_ClearsThePairing()
    {
        var preferences = new InMemoryRemoteComputePreferences();
        preferences.SavePairedHostSub("host-1");
        var sut = Create(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}")), lanBase: null, preferences);

        await sut.StopUsingRemoteAsync();

        Assert.False(sut.IsGuestPaired);
        Assert.Contains("Stopped using their local-service", sut.Status, StringComparison.Ordinal);
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

            return Json(HttpStatusCode.OK, """{"code":"HOST2345","expiresAt":1}""");
        });
        var sut = Create(handler, lanBase: "http://127.0.0.1:9", preferences);

        await sut.SetShareEnabledAsync(true);

        Assert.True(sut.ShareEnabled);
        Assert.False(sut.IsGuestPaired);
        Assert.Equal("HOST2345", sut.InviteCode);
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
        IAuthTokenProvider? tokens = null)
    {
        var lan = new Mock<ILocalLanPlayService>();
        lan.Setup(service => service.GetServiceBaseUrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(lanBase);
        return new RemoteComputeController(
            new HttpClient(handler),
            Options.Create(new APISettings { HeadlessBaseUrl = "https://api.test" }),
            lan.Object,
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
}
