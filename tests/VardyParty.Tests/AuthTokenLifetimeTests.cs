using System;
using AutoFixture;
using Xunit;

namespace VardyParty.Tests;

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
    public void MustRefreshBeforeUse_WhenAccessTokenStillValid_DoesNotBlock()
    {
        // Arrange
        const bool forceRefresh = false;
        const bool hasValidAccessToken = true;

        // Act
        var mustRefresh = AuthTokenLifetime.MustRefreshBeforeUse(forceRefresh, hasValidAccessToken);

        // Assert
        Assert.False(mustRefresh);
    }

    [Fact]
    public void MustRefreshBeforeUse_WhenForced_BlocksEvenIfAccessTokenValid()
    {
        // Arrange
        const bool forceRefresh = true;
        const bool hasValidAccessToken = true;

        // Act
        var mustRefresh = AuthTokenLifetime.MustRefreshBeforeUse(forceRefresh, hasValidAccessToken);

        // Assert
        Assert.True(mustRefresh);
    }

    [Fact]
    public void MustRefreshBeforeUse_WhenAccessTokenUnusable_Blocks()
    {
        // Arrange
        const bool forceRefresh = false;
        const bool hasValidAccessToken = false;

        // Act
        var mustRefresh = AuthTokenLifetime.MustRefreshBeforeUse(forceRefresh, hasValidAccessToken);

        // Assert
        Assert.True(mustRefresh);
    }

    [Fact]
    public void ShouldRefreshInBackground_WhenSlidingDueAndTokenValid_ReturnsTrue()
    {
        // Arrange
        const bool forceRefresh = false;
        const bool hasValidAccessToken = true;
        const bool refreshDue = true;

        // Act
        var background = AuthTokenLifetime.ShouldRefreshInBackground(forceRefresh, hasValidAccessToken, refreshDue);

        // Assert
        Assert.True(background);
    }

    [Fact]
    public void ShouldRefreshInBackground_WhenAccessTokenUnusable_ReturnsFalse()
    {
        // Arrange
        const bool forceRefresh = false;
        const bool hasValidAccessToken = false;
        const bool refreshDue = true;

        // Act
        var background = AuthTokenLifetime.ShouldRefreshInBackground(forceRefresh, hasValidAccessToken, refreshDue);

        // Assert
        Assert.False(background);
    }

    [Fact]
    public void ShouldRefreshInBackground_WhenForced_ReturnsFalse()
    {
        // Arrange
        const bool forceRefresh = true;
        const bool hasValidAccessToken = true;
        const bool refreshDue = true;

        // Act
        var background = AuthTokenLifetime.ShouldRefreshInBackground(forceRefresh, hasValidAccessToken, refreshDue);

        // Assert
        Assert.False(background);
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
