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
        IRemoteComputePreferences? preferences = null)
    {
        var lan = new Mock<ILocalLanPlayService>();
        lan.Setup(service => service.GetServiceBaseUrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(lanBase);
        return new RemoteComputeController(
            new HttpClient(handler),
            Options.Create(new APISettings { HeadlessBaseUrl = "https://api.test" }),
            lan.Object,
            preferences ?? new InMemoryRemoteComputePreferences(),
            NullLogger<RemoteComputeController>.Instance);
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
