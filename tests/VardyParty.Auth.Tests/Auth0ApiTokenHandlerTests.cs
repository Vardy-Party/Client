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
    public async Task SendAsync_TokenFetchDoesNotUseRequestCancellation()
    {
        // Arrange
        CancellationToken captured = default;
        var tokenProvider = _fixture.GetMock<IAuthTokenProvider>();
        tokenProvider
            .Setup(provider => provider.GetAccessTokenAsync(It.IsAny<CancellationToken>(), false))
            .Callback<CancellationToken, bool>((token, _) => captured = token)
            .ReturnsAsync(_fixture.Create<string>());

        var inner = new SequenceStatusHandler(HttpStatusCode.OK);
        var handler = new Auth0ApiTokenHandler(tokenProvider.Object, NullLogger<Auth0ApiTokenHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);
        using var requestCts = new CancellationTokenSource();

        // Act
        var response = await client.GetAsync("https://catalog.example.test/games", requestCts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(captured.Equals(requestCts.Token));
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
