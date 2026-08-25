using System;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace VardyParty.Tests;

public class Auth0TokenSessionTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private const string NorthgateRoleClaim = "https://northgate.test/roles";
    private const string OakLaneMember = "oak-lane-member";

    [Fact]
    public async Task PollDeviceLoginAsync_WhenAccessTokenLacksRole_DoesNotPersist()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var token = AuthAccessTokenRolesTests.CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":"scoreboard"}""");
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.ExchangeDeviceCodeAsync(settings, "device-oak", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0TokenHttpResult(true, token, 3600, "refresh-oak", null, null));
        var sut = CreateSession(settings, oauth.Object);

        // Act
        var result = await sut.PollDeviceLoginAsync(CreateDeviceCode(), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains(OakLaneMember, result.Error, StringComparison.Ordinal);
        Assert.False(sut.HasValidToken);
        Assert.False(sut.Persisted);
    }

    [Fact]
    public async Task PollDeviceLoginAsync_WhenAccessTokenHasRole_Persists()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var token = AuthAccessTokenRolesTests.CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":"{{OakLaneMember}}"}""");
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.ExchangeDeviceCodeAsync(settings, "device-oak", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0TokenHttpResult(true, token, 3600, "refresh-oak", null, null));
        var sut = CreateSession(settings, oauth.Object);

        // Act
        var result = await sut.PollDeviceLoginAsync(CreateDeviceCode(), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(sut.HasValidToken);
        Assert.True(sut.Persisted);
    }

    [Fact]
    public async Task StartDeviceLoginAsync_WhenClientIdMissingAndThrowEnabled_Throws()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        settings.ClientId = string.Empty;
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        var sut = CreateSession(settings, oauth.Object, throwOnMissingDeviceConfig: true);

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartDeviceLoginAsync(CancellationToken.None));

        // Assert
        Assert.Contains("not configured", thrown.Message, StringComparison.OrdinalIgnoreCase);
        oauth.Verify(
            client => client.RequestDeviceCodeAsync(It.IsAny<Auth0Settings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartDeviceLoginAsync_WhenClientIdMissingAndThrowDisabled_ReturnsNull()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        settings.ClientId = string.Empty;
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        var sut = CreateSession(settings, oauth.Object, throwOnMissingDeviceConfig: false);

        // Act
        var result = await sut.StartDeviceLoginAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
        oauth.Verify(
            client => client.RequestDeviceCodeAsync(It.IsAny<Auth0Settings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartDeviceLoginAsync_WhenDeviceCodeRequestFailsAndThrowEnabled_Throws()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.RequestDeviceCodeAsync(settings, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0DeviceCodeHttpResult(false, null, "oak-lane device grant denied", "{}"));
        var sut = CreateSession(settings, oauth.Object, throwOnMissingDeviceConfig: true);

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartDeviceLoginAsync(CancellationToken.None));

        // Assert
        Assert.Contains("oak-lane device grant denied", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartDeviceLoginAsync_WhenDeviceCodeRequestFailsAndThrowDisabled_ReturnsNull()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.RequestDeviceCodeAsync(settings, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0DeviceCodeHttpResult(false, null, "oak-lane device grant denied", "{}"));
        var sut = CreateSession(settings, oauth.Object, throwOnMissingDeviceConfig: false);

        // Act
        var result = await sut.StartDeviceLoginAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenSlidingRefreshDue_ReturnsCurrentTokenWithoutWaiting()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var currentAccess = _fixture.Create<string>();
        var rotatedAccess = _fixture.Create<string>();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshHold = new TaskCompletionSource<Auth0TokenHttpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.RefreshAsync(settings, "oak-lane-refresh", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                refreshStarted.TrySetResult();
                return refreshHold.Task;
            });
        var sut = CreateSession(settings, oauth.Object);
        sut.SeedTokens(
            currentAccess,
            "oak-lane-refresh",
            expiresAt: DateTimeOffset.UtcNow.AddHours(2),
            lastRefreshedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        // Act
        var token = await sut.GetAccessTokenAsync(CancellationToken.None, forceRefresh: false);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(currentAccess, token);
        Assert.False(refreshHold.Task.IsCompleted);
        refreshHold.SetResult(new Auth0TokenHttpResult(true, rotatedAccess, 3600, "oak-lane-refresh", null, null));
        await sut.PersistGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(rotatedAccess, sut.VisibleAccessToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenForcedRefresh_WaitsForNewAccessToken()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var currentAccess = _fixture.Create<string>();
        var rotatedAccess = _fixture.Create<string>();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshHold = new TaskCompletionSource<Auth0TokenHttpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        oauth
            .Setup(client => client.RefreshAsync(settings, "oak-lane-refresh", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                refreshStarted.TrySetResult();
                return refreshHold.Task;
            });
        var sut = CreateSession(settings, oauth.Object);
        sut.SeedTokens(
            currentAccess,
            "oak-lane-refresh",
            expiresAt: DateTimeOffset.UtcNow.AddHours(2),
            lastRefreshedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        // Act
        var getTask = sut.GetAccessTokenAsync(CancellationToken.None, forceRefresh: true);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(getTask.IsCompleted);
        refreshHold.SetResult(new Auth0TokenHttpResult(true, rotatedAccess, 3600, "oak-lane-refresh", null, null));
        var token = await getTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(rotatedAccess, token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DuringInteractiveBrowserWait_DoesNotBlock()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var oauth = _fixture.GetMock<IAuth0OAuthClient>();
        var browserHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = _fixture.GetMock<IOptions<Auth0Settings>>();
        options.Setup(source => source.Value).Returns(settings);
        var sut = new BrowserHoldAuthSession(NullLogger.Instance, options.Object, oauth.Object, browserHold);

        // Act
        var loginTask = sut.LoginInteractiveAsync(CancellationToken.None);
        var token = await sut.GetAccessTokenAsync(CancellationToken.None);

        // Assert
        Assert.Null(token);
        Assert.False(loginTask.IsCompleted);
        browserHold.SetResult();
        var login = await loginTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(login.IsSuccess);
        Assert.Equal("oak-lane-browser", login.AccessToken);
    }

    private MemoryAuthSession CreateSession(
        Auth0Settings settings,
        IAuth0OAuthClient oauth,
        bool throwOnMissingDeviceConfig = true)
    {
        var options = _fixture.GetMock<IOptions<Auth0Settings>>();
        options.Setup(source => source.Value).Returns(settings);
        return new MemoryAuthSession(NullLogger.Instance, options.Object, oauth, throwOnMissingDeviceConfig);
    }

    private Auth0Settings CreateNorthgateSettings()
        => _fixture.Build<Auth0Settings>()
            .With(settings => settings.Domain, "id.northgate.test")
            .With(settings => settings.ClientId, "northgate-desktop")
            .With(settings => settings.Audience, "https://catalog.northgate.test")
            .With(settings => settings.Scope, "openid profile")
            .With(settings => settings.TokenLeewaySeconds, 60)
            .With(settings => settings.SlidingRefreshAfterSeconds, 60)
            .With(settings => settings.RequiredRoleClaimType, NorthgateRoleClaim)
            .With(settings => settings.RequiredRole, OakLaneMember)
            .Create();

    private static AuthDeviceCode CreateDeviceCode()
        => new(
            "device-oak",
            "WD4K-7Q2P",
            "https://id.northgate.test/activate",
            null,
            600,
            5,
            DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class MemoryAuthSession(
        ILogger logger,
        IOptions<Auth0Settings> settings,
        IAuth0OAuthClient oauth,
        bool throwOnMissingDeviceConfig = true) : Auth0TokenSession(logger, settings, oauth)
    {
        public bool Persisted { get; private set; }

        public string? VisibleAccessToken => AccessToken;

        public TaskCompletionSource PersistGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override bool ThrowOnMissingDeviceConfig => throwOnMissingDeviceConfig;

        public void SeedTokens(
            string accessToken,
            string refreshToken,
            DateTimeOffset expiresAt,
            DateTimeOffset lastRefreshedAt)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            ExpiresAt = expiresAt;
            LastRefreshedAt = lastRefreshedAt;
            TokenLoaded = true;
        }

        public override Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AuthLoginResult(false, null, "unused"));

        protected override Task LoadPersistedTokensAsync() => Task.CompletedTask;

        protected override Task PersistTokensAsync()
        {
            Persisted = true;
            PersistGate.TrySetResult();
            return Task.CompletedTask;
        }

        protected override Task ClearPersistedTokensAsync() => Task.CompletedTask;
    }

    private sealed class BrowserHoldAuthSession(
        ILogger logger,
        IOptions<Auth0Settings> settings,
        IAuth0OAuthClient oauth,
        TaskCompletionSource browserHold) : Auth0TokenSession(logger, settings, oauth)
    {
        public override async Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
        {
            await browserHold.Task;
            await ApplyTokensAsync("oak-lane-browser", 3600, "oak-lane-refresh");
            return new AuthLoginResult(true, AccessToken, null);
        }

        protected override Task LoadPersistedTokensAsync() => Task.CompletedTask;

        protected override Task PersistTokensAsync() => Task.CompletedTask;

        protected override Task ClearPersistedTokensAsync() => Task.CompletedTask;
    }
}
