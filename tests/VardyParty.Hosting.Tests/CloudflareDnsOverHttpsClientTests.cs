using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using VardyParty.Hosting;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Hosting.Tests;

public class CloudflareDnsOverHttpsClientTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task ResolveAsync_WhenAaaaFails_ReturnsSuccessfulARecord()
    {
        // Arrange
        var host = _fixture.Create<string>();
        var ipv4 = new IPAddress([192, 0, 2, 10]);
        using var http = new HttpClient(new SplitRecordHandler(ipv4))
        {
            BaseAddress = new Uri($"https://{CloudflareDnsOverHttpsClient.ResolverHostName}/")
        };
        using var sut = new CloudflareDnsOverHttpsClient(http);

        // Act
        var addresses = await sut.ResolveAsync(host);

        // Assert
        Assert.Equal([ipv4], addresses);
    }

    [Fact]
    public async Task ResolveAsync_WhenAaaaTimesOut_ReturnsSuccessfulARecord()
    {
        // Arrange
        var host = _fixture.Create<string>();
        var ipv4 = new IPAddress([192, 0, 2, 11]);
        using var http = new HttpClient(new SplitTimeoutHandler(ipv4))
        {
            BaseAddress = new Uri($"https://{CloudflareDnsOverHttpsClient.ResolverHostName}/")
        };
        using var sut = new CloudflareDnsOverHttpsClient(http);

        // Act
        var addresses = await sut.ResolveAsync(host);

        // Assert
        Assert.Equal([ipv4], addresses);
    }

    [Fact]
    public async Task ResolveAsync_WhenAAndAaaaFail_Throws()
    {
        // Arrange
        var host = _fixture.Create<string>();
        using var http = new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri($"https://{CloudflareDnsOverHttpsClient.ResolverHostName}/")
        };
        using var sut = new CloudflareDnsOverHttpsClient(http);

        // Act
        var ex = await Assert.ThrowsAsync<AggregateException>(() => sut.ResolveAsync(host));

        // Assert
        Assert.Equal(2, ex.InnerExceptions.Count);
    }

    private sealed class SplitRecordHandler(IPAddress ipv4) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("type=28", StringComparison.Ordinal))
            {
                throw new HttpRequestException("AAAA query failed");
            }

            var json = $$"""{"Status":0,"Answer":[{"type":1,"data":"{{ipv4}}"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SplitTimeoutHandler(IPAddress ipv4) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("type=28", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("AAAA query timed out");
            }

            var json = $$"""{"Status":0,"Answer":[{"type":1,"data":"{{ipv4}}"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("DoH query failed");
        }
    }
}
