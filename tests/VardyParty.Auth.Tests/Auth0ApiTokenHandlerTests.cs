using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using VardyParty.Auth;
using VardyParty.TestSupport;

namespace VardyParty.Auth.Tests;

public class Auth0ApiTokenHandlerTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task SendAsync_WhenUnauthorized_RetriesOnceAfterForcedRefresh()
    {
        // Arrange
        var original = _fixture.Create<string>();
        var rotated = _fixture.Create<string>();
        var tokenProvider = _fixture.GetMock<IAuthTokenProvider>();
        tokenProvider
            .Setup(provider => provider.GetAccessTokenAsync(It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(original);
        tokenProvider
            .Setup(provider => provider.GetAccessTokenAsync(It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(rotated);

        var inner = new SequenceStatusHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var handler = new Auth0ApiTokenHandler(tokenProvider.Object, NullLogger<Auth0ApiTokenHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://catalog.example.test/games");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.SendCount);
        tokenProvider.Verify(
            provider => provider.GetAccessTokenAsync(It.IsAny<CancellationToken>(), true),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_WhenRefreshReturnsSameToken_DoesNotRetry()
    {
        // Arrange
        var token = _fixture.Create<string>();
        var tokenProvider = _fixture.GetMock<IAuthTokenProvider>();
        tokenProvider
            .Setup(provider => provider.GetAccessTokenAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(token);

        var inner = new SequenceStatusHandler(HttpStatusCode.Unauthorized);
        var handler = new Auth0ApiTokenHandler(tokenProvider.Object, NullLogger<Auth0ApiTokenHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://catalog.example.test/games");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, inner.SendCount);
    }

    [Fact]
    public async Task SendAsync_HealthCheck_DoesNotFetchAccessToken()
    {
        // Arrange
        var tokenProvider = _fixture.GetMock<IAuthTokenProvider>();
        var inner = new SequenceStatusHandler(HttpStatusCode.OK);
        var handler = new Auth0ApiTokenHandler(tokenProvider.Object, NullLogger<Auth0ApiTokenHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://localservice.example.test:5019/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.SendCount);
        tokenProvider.Verify(
            provider => provider.GetAccessTokenAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Theory]
    [InlineData("/health", false)]
    [InlineData("/Health", false)]
    [InlineData("/league-alpha/home-united-vs-away-city/health", true)]
    [InlineData("/mp", true)]
    [InlineData("/play/https%3A%2F%2Fexample.test", true)]
    public void ShouldAttachAccessToken_MatchesLocalServicePaths(string path, bool expected)
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:5019{path}");

        // Act
        var attach = Auth0ApiTokenHandler.ShouldAttachAccessToken(request);

        // Assert
        Assert.Equal(expected, attach);
    }

    [Fact]
    public async Task SendAsync_TokenFetchRespectsRequestCancellation()
    {
        // Arrange — token fetch is linked to the request token so Cancel on
        // finding-streams can abort a hung Auth0 refresh before /mp starts.
        var tokenFetchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenProvider = _fixture.GetMock<IAuthTokenProvider>();
        tokenProvider
            .Setup(provider => provider.GetAccessTokenAsync(It.IsAny<CancellationToken>(), false))
            .Returns(async (CancellationToken token, bool _) =>
            {
                tokenFetchEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return _fixture.Create<string>();
            });

        var inner = new SequenceStatusHandler(HttpStatusCode.OK);
        var handler = new Auth0ApiTokenHandler(tokenProvider.Object, NullLogger<Auth0ApiTokenHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);
        using var requestCts = new CancellationTokenSource();

        // Act
        var sendTask = client.GetAsync("https://catalog.example.test/games", requestCts.Token);
        await tokenFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestCts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.Equal(0, inner.SendCount);
    }

    private sealed class SequenceStatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _codes;
        private int _index;

        public SequenceStatusHandler(params HttpStatusCode[] codes)
        {
            _codes = codes;
        }

        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            var status = _codes[_index];
            if (_index < _codes.Length - 1)
            {
                _index++;
            }

            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }
}
