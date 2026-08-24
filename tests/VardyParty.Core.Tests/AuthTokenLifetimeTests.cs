using System;
using AutoFixture;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class AuthTokenLifetimeTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void EnsureOfflineAccess_AddsRefreshScopeWithoutDroppingConfiguredScopes()
    {
        // Arrange
        var configured = "openid profile";

        // Act
        var scope = AuthTokenLifetime.EnsureOfflineAccess(configured);

        // Assert
        Assert.Contains("openid", scope, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile", scope, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email", scope, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offline_access", scope, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldRefreshAccessToken_WhenNearExpiry_ReturnsTrue()
    {
        // Arrange
        var now = DateTimeOffset.Parse("2026-04-12T15:00:00Z");
        var expiresAt = now.AddSeconds(30);
        var lastRefreshedAt = now.AddMinutes(-1);

        // Act
        var refresh = AuthTokenLifetime.ShouldRefreshAccessToken(
            expiresAt,
            now,
            leewaySeconds: 60,
            lastRefreshedAt,
            slidingRefreshAfterSeconds: 900);

        // Assert
        Assert.True(refresh);
    }

    [Fact]
    public void ShouldRefreshAccessToken_WhenStillFresh_ReturnsFalse()
    {
        // Arrange
        var now = DateTimeOffset.Parse("2026-04-12T15:00:00Z");
        var expiresAt = now.AddHours(12);
        var lastRefreshedAt = now.AddMinutes(-2);

        // Act
        var refresh = AuthTokenLifetime.ShouldRefreshAccessToken(
            expiresAt,
            now,
            leewaySeconds: 60,
            lastRefreshedAt,
            slidingRefreshAfterSeconds: 900);

        // Assert
        Assert.False(refresh);
    }

    [Fact]
    public void ShouldRefreshAccessToken_WhenSlidingWindowElapsed_ReturnsTrue()
    {
        // Arrange
        var now = DateTimeOffset.Parse("2026-04-12T15:00:00Z");
        var expiresAt = now.AddHours(12);
        var lastRefreshedAt = now.AddMinutes(-20);
        var slidingAfterSeconds = AuthTokenLifetime.DefaultSlidingRefreshAfterSeconds;

        // Act
        var refresh = AuthTokenLifetime.ShouldRefreshAccessToken(
            expiresAt,
            now,
            leewaySeconds: 60,
            lastRefreshedAt,
            slidingRefreshAfterSeconds: slidingAfterSeconds);

        // Assert
        Assert.True(refresh);
    }

    [Fact]
    public void CoalesceRefreshToken_KeepsExistingWhenIncomingBlank()
    {
        // Arrange
        var existing = _fixture.Create<string>();

        // Act
        var kept = AuthTokenLifetime.CoalesceRefreshToken(null, existing);
        var replaced = AuthTokenLifetime.CoalesceRefreshToken("new-refresh", existing);

        // Assert
        Assert.Equal(existing, kept);
        Assert.Equal("new-refresh", replaced);
    }

    [Fact]
    public void IsRefreshRejected_OnlyForInvalidGrant()
    {
        // Arrange
        var transient = "temporarily_unavailable";

        // Act
        var rejected = AuthTokenLifetime.IsRefreshRejected("invalid_grant");
        var network = AuthTokenLifetime.IsRefreshRejected(transient);
        var missing = AuthTokenLifetime.IsRefreshRejected(null);

        // Assert
        Assert.True(rejected);
        Assert.False(network);
        Assert.False(missing);
    }
}
