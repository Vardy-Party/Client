using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using VardyParty.Auth;
using VardyParty.Hosting;

namespace VardyParty.Tests;

public class NonDisposingDelegatingHandlerTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void Dispose_DoesNotDisposeInnerHandler()
    {
        // Arrange
        var inner = new TrackingHandler();
        var sut = new NonDisposingDelegatingHandler(inner);

        // Act
        sut.Dispose();

        // Assert
        Assert.False(inner.Disposed);
    }

    [Fact]
    public async Task SendAsync_DelegatesToInnerHandler()
    {
        // Arrange
        var path = _fixture.Create<string>();
        var inner = new TrackingHandler();
        var sut = new NonDisposingDelegatingHandler(inner);
        using var client = new HttpClient(sut, disposeHandler: false);

        // Act
        var response = await client.GetAsync($"https://catalog.northgate.test/{path}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.SendCount);
    }

    [Fact]
    public void AddVardyPartyHttpClients_RegistersDualStackPlaybackProbe()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddVardyPartyHttpClients();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        using var probe = factory.CreateClient(PlaybackHttpClients.Probe);
        using var auth0 = factory.CreateClient(Auth0HttpClients.Name);

        // Assert
        Assert.Equal(PlaybackHttpClients.ProbeTimeout, probe.Timeout);
        Assert.NotNull(auth0);
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
