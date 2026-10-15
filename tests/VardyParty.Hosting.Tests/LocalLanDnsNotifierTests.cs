using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Hosting;
using VardyParty.Ports;
using VardyParty.Streaming;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class LocalLanDnsNotifierTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void AddVardyPartyHttpClients_RegistersShortLivedDnsNotifier()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddVardyPartyHttpClients();

        // Assert
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ILocalLanDnsNotifier));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(LocalLanDnsNotifier), descriptor.ImplementationType);
    }

    [Fact]
    public async Task NotifyAsync_ResolvesPlayClientAndHttpClientPerCall()
    {
        // Arrange
        var play = _fixture.GetMock<ILocalLanPlayService>();
        play.Setup(p => p.GetServiceBaseUrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://127.0.0.1:4019");
        var preferences = _fixture.GetMock<IDnsPreferencesStore>();
        preferences.Setup(p => p.LoadDnsOverHttpsFallbackEnabled()).Returns(true);
        var endpoint = _fixture.GetMock<IDnsOverHttpsEndpoint>();
        endpoint.Setup(e => e.Address).Returns(IPAddress.Parse("192.0.2.53"));

        var scopes = new CountingScopeFactory(play.Object);
        var handler = new RecordingHandler();
        var http = new CountingHttpClientFactory(handler);
        var sut = new LocalLanDnsNotifier(
            scopes,
            http,
            NullLogger<LocalLanDnsNotifier>.Instance,
            preferences.Object,
            endpoint.Object);

        // Act
        await sut.NotifyAsync(true);
        await sut.NotifyAsync(false);

        // Assert
        Assert.Equal(2, scopes.ScopesCreated);
        Assert.Equal(2, http.ClientsCreated);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.Equal("http://127.0.0.1:4019/dns", request.RequestUri?.ToString()));
    }

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private readonly ILocalLanPlayService _play;

        public CountingScopeFactory(ILocalLanPlayService play) => _play = play;

        public int ScopesCreated { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopesCreated++;
            return new Scope(_play);
        }

        private sealed class Scope : IServiceScope
        {
            public Scope(ILocalLanPlayService play) => ServiceProvider = new Provider(play);

            public IServiceProvider ServiceProvider { get; }

            public void Dispose()
            {
            }

            private sealed class Provider : IServiceProvider
            {
                private readonly ILocalLanPlayService _play;

                public Provider(ILocalLanPlayService play) => _play = play;

                public object? GetService(Type serviceType) =>
                    serviceType == typeof(ILocalLanPlayService) ? _play : null;
            }
        }
    }

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public CountingHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public int ClientsCreated { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(LocalLanPlayHttpClient.Name, name);
            ClientsCreated++;
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
