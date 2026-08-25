using System;
using System.Collections.Generic;

namespace VardyParty.Services;

/// <summary>
/// Access-token expiry and sliding refresh rules shared by MAUI and Linux Auth0 hosts.
/// </summary>
public static class AuthTokenLifetime
{
    public const int DefaultLeewaySeconds = 60;
    public const int DefaultSlidingRefreshAfterSeconds = 15 * 60;

    public static string EnsureOfflineAccess(string? configuredScope)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "openid",
            "profile",
            "email",
            "offline_access"
        };

        if (!string.IsNullOrWhiteSpace(configuredScope))
        {
            foreach (var item in configuredScope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                scopes.Add(item.Trim());
            }
        }

        return string.Join(" ", scopes);
    }

    public static bool ShouldRefreshAccessToken(
        DateTimeOffset expiresAt,
        DateTimeOffset utcNow,
        int leewaySeconds,
        DateTimeOffset lastRefreshedAt,
        int slidingRefreshAfterSeconds)
    {
        if (expiresAt == DateTimeOffset.MinValue)
        {
            return true;
        }

        var leeway = Math.Abs(leewaySeconds);
        if (expiresAt <= utcNow.AddSeconds(leeway))
        {
            return true;
        }

        if (slidingRefreshAfterSeconds <= 0 || lastRefreshedAt == DateTimeOffset.MinValue)
        {
            return false;
        }

        return utcNow - lastRefreshedAt >= TimeSpan.FromSeconds(slidingRefreshAfterSeconds);
    }

    public static string? CoalesceRefreshToken(string? incoming, string? existing)
        => string.IsNullOrWhiteSpace(incoming) ? existing : incoming;

    public static bool IsRefreshRejected(string? oauthError)
        => string.Equals(oauthError, "invalid_grant", StringComparison.OrdinalIgnoreCase)
           || string.Equals(oauthError, "invalid_token", StringComparison.OrdinalIgnoreCase);
}

public enum AuthTokenRefreshOutcome
{
    Success,
    TransientFailure,
    Rejected
}
