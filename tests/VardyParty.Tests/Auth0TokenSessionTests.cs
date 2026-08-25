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
        var options = _fixture.GetMock<IOptions<Auth0Settings>>();
        options.Setup(source => source.Value).Returns(settings);
        var sut = new MemoryAuthSession(NullLogger.Instance, options.Object, oauth.Object);

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
        var options = _fixture.GetMock<IOptions<Auth0Settings>>();
        options.Setup(source => source.Value).Returns(settings);
        var sut = new MemoryAuthSession(NullLogger.Instance, options.Object, oauth.Object);

        // Act
        var result = await sut.PollDeviceLoginAsync(CreateDeviceCode(), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(sut.HasValidToken);
        Assert.True(sut.Persisted);
    }

    private Auth0Settings CreateNorthgateSettings()
        => _fixture.Build<Auth0Settings>()
            .With(settings => settings.Domain, "id.northgate.test")
            .With(settings => settings.ClientId, "northgate-desktop")
            .With(settings => settings.Audience, "https://catalog.northgate.test")
            .With(settings => settings.Scope, "openid profile")
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
        IAuth0OAuthClient oauth) : Auth0TokenSession(logger, settings, oauth)
    {
        public bool Persisted { get; private set; }

        public override Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AuthLoginResult(false, null, "unused"));

        protected override Task LoadPersistedTokensAsync() => Task.CompletedTask;

        protected override Task PersistTokensAsync()
        {
            Persisted = true;
            return Task.CompletedTask;
        }

        protected override Task ClearPersistedTokensAsync() => Task.CompletedTask;
    }
}
