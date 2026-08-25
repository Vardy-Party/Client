using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Auth;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Auth.Tests;

public class Auth0PkceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private const string NorthgateRoleClaim = "https://northgate.test/roles";
    private const string OakLaneMember = "oak-lane-member";

    [Fact]
    public void Start_BuildsAuthorizeUrlWithS256Challenge()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var redirect = new Uri("http://127.0.0.1:4280/callback");

        // Act
        var start = Auth0Pkce.Start(settings, redirect);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(start.State));
        Assert.False(string.IsNullOrWhiteSpace(start.CodeVerifier));
        Assert.Equal(Auth0Pkce.CreateCodeChallenge(start.CodeVerifier), start.CodeChallenge);
        Assert.StartsWith("https://id.northgate.test/authorize?", start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains("response_type=code", start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains($"code_challenge={Uri.EscapeDataString(start.CodeChallenge)}", start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains($"state={Uri.EscapeDataString(start.State)}", start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains("offline_access", start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("https://catalog.northgate.test"), start.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(redirect.ToString()), start.AuthorizeUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCodeChallenge_UsesS256OfAsciiVerifier()
    {
        // Arrange
        var verifier = "oak-lane-pkce-verifier";
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // Act
        var challenge = Auth0Pkce.CreateCodeChallenge(verifier);

        // Assert
        Assert.Equal(expected, challenge);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4280/callback", true)]
    [InlineData("http://localhost:4280/callback", true)]
    [InlineData("https://localhost/callback", true)]
    [InlineData("https://id.northgate.test/callback", false)]
    [InlineData("vardyparty://callback", false)]
    [InlineData("", false)]
    public void TryGetLoopbackRedirectUri_AcceptsOnlyLoopbackHttp(string redirectUri, bool expected)
    {
        // Arrange
        var candidate = redirectUri;

        // Act
        var accepted = Auth0Pkce.TryGetLoopbackRedirectUri(candidate, out var uri);

        // Assert
        Assert.Equal(expected, accepted);
        if (expected)
            Assert.Equal(candidate, uri.OriginalString);
    }

    [Fact]
    public void BuildListenerPrefix_UsesEffectivePortAndTrailingSlash()
    {
        // Arrange
        var loopback = new Uri("http://127.0.0.1:4280/callback");
        var httpsDefault = new Uri("https://localhost/callback");
        var root = new Uri("http://127.0.0.1:4280/");

        // Act
        var loopbackPrefix = Auth0Pkce.BuildListenerPrefix(loopback);
        var httpsPrefix = Auth0Pkce.BuildListenerPrefix(httpsDefault);
        var rootPrefix = Auth0Pkce.BuildListenerPrefix(root);

        // Assert
        Assert.Equal("http://127.0.0.1:4280/callback/", loopbackPrefix);
        Assert.Equal("https://localhost:443/callback/", httpsPrefix);
        Assert.Equal("http://127.0.0.1:4280/", rootPrefix);
    }

    [Fact]
    public void DescribeCallbackFailure_MapsErrorStateAndMissingCode()
    {
        // Arrange
        const string expectedState = "oak-lane-state";

        // Act
        var oauthError = Auth0Pkce.DescribeCallbackFailure(
            expectedState, expectedState, "oak-code", "access_denied", "northgate denied");
        var stateMismatch = Auth0Pkce.DescribeCallbackFailure(
            expectedState, "other-state", "oak-code", null, null);
        var missingCode = Auth0Pkce.DescribeCallbackFailure(
            expectedState, expectedState, " ", null, null);
        var ok = Auth0Pkce.DescribeCallbackFailure(
            expectedState, expectedState, "oak-code", null, null);

        // Assert
        Assert.Equal("northgate denied", oauthError);
        var errorOnly = Auth0Pkce.DescribeCallbackFailure(
            expectedState, expectedState, "oak-code", "access_denied", null);
        Assert.Equal("access_denied", errorOnly);
        Assert.Equal("Auth0 callback state mismatch.", stateMismatch);
        Assert.Equal("Auth0 callback did not include authorization code.", missingCode);
        Assert.Null(ok);
    }

    [Fact]
    public async Task CompleteAuthorizationCodeAsync_WhenAccessTokenLacksRole_DoesNotPersist()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var token = AuthAccessTokenRolesTests.CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":"scoreboard"}""");
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.ExchangeAuthorizationCodeAsync(
                settings,
                "oak-code",
                "http://127.0.0.1:4280/callback",
                "oak-verifier",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0TokenHttpResult(true, token, 3600, "refresh-oak", null, null));
        var sut = CreatePkceSession(settings, oauth.Object);

        // Act
        var result = await sut.CompletePkceAsync();

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains(OakLaneMember, result.Error, StringComparison.Ordinal);
        Assert.False(sut.HasValidToken);
        Assert.False(sut.Persisted);
    }

    [Fact]
    public async Task CompleteAuthorizationCodeAsync_WhenAccessTokenHasRole_Persists()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var token = AuthAccessTokenRolesTests.CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":"{{OakLaneMember}}"}""");
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.ExchangeAuthorizationCodeAsync(
                settings,
                "oak-code",
                "http://127.0.0.1:4280/callback",
                "oak-verifier",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0TokenHttpResult(true, token, 3600, "refresh-oak", null, null));
        var sut = CreatePkceSession(settings, oauth.Object);

        // Act
        var result = await sut.CompletePkceAsync();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(sut.HasValidToken);
        Assert.True(sut.Persisted);
        Assert.Equal(token, result.AccessToken);
    }

    [Fact]
    public async Task CompleteAuthorizationCodeAsync_WhenExchangeFails_DoesNotPersist()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.ExchangeAuthorizationCodeAsync(
                settings,
                "oak-code",
                "http://127.0.0.1:4280/callback",
                "oak-verifier",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0TokenHttpResult(false, null, 0, null, "invalid_grant", "oak-lane rejected"));
        var sut = CreatePkceSession(settings, oauth.Object);

        // Act
        var result = await sut.CompletePkceAsync();

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("oak-lane rejected", result.Error);
        Assert.False(sut.HasValidToken);
        Assert.False(sut.Persisted);
    }

    [Fact]
    public async Task CompleteAuthorizationCodeAsync_WhenExchangeThrows_DoesNotPersist()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.ExchangeAuthorizationCodeAsync(
                settings,
                "oak-code",
                "http://127.0.0.1:4280/callback",
                "oak-verifier",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("northgate unreachable"));
        var sut = CreatePkceSession(settings, oauth.Object);

        // Act
        var result = await sut.CompletePkceAsync();

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("northgate unreachable", result.Error, StringComparison.Ordinal);
        Assert.False(sut.HasValidToken);
        Assert.False(sut.Persisted);
    }

    [Fact]
    public async Task LoginInteractiveAsync_DuringPkceBrowserWait_DoesNotBlockGetAccessToken()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var token = AuthAccessTokenRolesTests.CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":"{{OakLaneMember}}"}""");
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.ExchangeAuthorizationCodeAsync(
                settings,
                "oak-code",
                "http://127.0.0.1:4280/callback",
                "oak-verifier",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0TokenHttpResult(true, token, 3600, "refresh-oak", null, null));
        var browserHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = CreatePkceSession(settings, oauth.Object, browserHold);

        // Act
        var loginTask = sut.LoginInteractiveAsync(CancellationToken.None);
        var access = await sut.GetAccessTokenAsync(CancellationToken.None);

        // Assert
        Assert.Null(access);
        Assert.False(loginTask.IsCompleted);
        browserHold.SetResult();
        var login = await loginTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(login.IsSuccess);
        Assert.Equal(token, login.AccessToken);
    }

    private PkceAuthSession CreatePkceSession(
        Auth0Settings settings,
        IAuth0OAuthClient oauth,
        TaskCompletionSource? browserHold = null)
    {
        var options = _fixture.GetMock<IOptions<Auth0Settings>>();
        options.Setup(source => source.Value).Returns(settings);
        return new PkceAuthSession(
            NullLogger.Instance,
            options.Object,
            oauth,
            browserHold ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private Auth0Settings CreateNorthgateSettings()
        => _fixture.Build<Auth0Settings>()
            .With(settings => settings.Domain, "id.northgate.test")
            .With(settings => settings.ClientId, "northgate-desktop")
            .With(settings => settings.Audience, "https://catalog.northgate.test")
            .With(settings => settings.Scope, "openid profile")
            .With(settings => settings.RedirectUri, "http://127.0.0.1:4280/callback")
            .With(settings => settings.TokenLeewaySeconds, 60)
            .With(settings => settings.SlidingRefreshAfterSeconds, 60)
            .With(settings => settings.RequiredRoleClaimType, NorthgateRoleClaim)
            .With(settings => settings.RequiredRole, OakLaneMember)
            .Create();

    private sealed class PkceAuthSession(
        ILogger logger,
        IOptions<Auth0Settings> settings,
        IAuth0OAuthClient oauth,
        TaskCompletionSource browserHold) : Auth0TokenSession(logger, settings, oauth)
    {
        public bool Persisted { get; private set; }

        public Task<AuthLoginResult> CompletePkceAsync()
            => CompleteAuthorizationCodeAsync(
                "oak-code",
                "http://127.0.0.1:4280/callback",
                "oak-verifier",
                CancellationToken.None);

        public override async Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
        {
            await EnsureTokenReadyAsync(cancellationToken, forceRefresh: false);
            if (HasValidToken)
                return new AuthLoginResult(true, AccessToken, null);

            await browserHold.Task.WaitAsync(cancellationToken);
            return await CompleteAuthorizationCodeAsync(
                "oak-code",
                "http://127.0.0.1:4280/callback",
                "oak-verifier",
                cancellationToken);
        }

        protected override Task LoadPersistedTokensAsync() => Task.CompletedTask;

        protected override Task PersistTokensAsync()
        {
            Persisted = true;
            return Task.CompletedTask;
        }

        protected override Task ClearPersistedTokensAsync()
        {
            Persisted = false;
            return Task.CompletedTask;
        }
    }
}
