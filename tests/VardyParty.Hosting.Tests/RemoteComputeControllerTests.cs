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
    public async Task Share_WhenNoLocalService_KeepsTheCodeAndDoesNotShare()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"code":"ABCD2345","expiresAt":1}"""));
        var preferences = new InMemoryRemoteComputePreferences();
        var sut = Create(handler, lanBase: null, preferences);

        await sut.SetShareEnabledAsync(true);

        Assert.False(sut.ShareEnabled);
        Assert.Equal("ABCD2345", sut.InviteCode);
        Assert.Contains("Local service failed", sut.Status, StringComparison.Ordinal);
        Assert.Contains("No local service", sut.Status, StringComparison.Ordinal);
        Assert.Contains("Correlation id:", sut.Status, StringComparison.Ordinal);
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
