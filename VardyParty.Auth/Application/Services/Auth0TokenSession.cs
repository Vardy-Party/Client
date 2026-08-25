using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;

namespace VardyParty.Auth;

/// <summary>
/// Shared Auth0 access-token session. Hosts persist tokens and own interactive login.
/// </summary>
public abstract class Auth0TokenSession : IAuthTokenProvider, IAuthLoginService
{
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private int _backgroundRefreshGate;

    protected Auth0TokenSession(
        ILogger logger,
        IOptions<Auth0Settings> auth0Settings,
        IAuth0OAuthClient oauth)
    {
        Logger = logger;
        Settings = auth0Settings.Value;
        Oauth = oauth;
    }

    protected ILogger Logger { get; }
    protected IAuth0OAuthClient Oauth { get; }
    protected Auth0Settings Settings { get; set; }
    protected string? AccessToken { get; set; }
    protected string? RefreshToken { get; set; }
    protected DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MinValue;
    protected DateTimeOffset LastRefreshedAt { get; set; } = DateTimeOffset.MinValue;
    protected bool TokenLoaded { get; set; }

    public bool HasValidToken => !string.IsNullOrWhiteSpace(AccessToken) && !IsExpired();

    public abstract Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default);

    protected virtual bool ThrowOnMissingDeviceConfig => true;

    protected virtual bool AcceptAccessToken(string accessToken)
    {
        if (AuthAccessTokenRoles.HasRequiredRole(accessToken, Settings.RequiredRoleClaimType, Settings.RequiredRole))
            return true;

        Logger.LogWarning("[Auth0] Access token missing required role '{RequiredRole}'", Settings.RequiredRole);
        return false;
    }

    public async Task<AuthDeviceLoginResult?> StartDeviceLoginAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Settings.ClientId) || string.IsNullOrWhiteSpace(Settings.Domain))
        {
            Logger.LogWarning("[Auth0] Device login missing ClientId/Domain");
            if (ThrowOnMissingDeviceConfig)
                throw new InvalidOperationException("Auth0 is not configured on this device build.");
            return null;
        }

        var result = await Oauth.RequestDeviceCodeAsync(Settings, cancellationToken);
        if (!result.IsSuccess || result.DeviceCode is null)
        {
            if (ThrowOnMissingDeviceConfig)
                throw new InvalidOperationException(result.Error ?? "Auth0 device sign-in failed.");
            return null;
        }

        return new AuthDeviceLoginResult(result.DeviceCode);
    }

    public async Task<AuthLoginResult> PollDeviceLoginAsync(
        AuthDeviceCode deviceCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Settings.ClientId) || string.IsNullOrWhiteSpace(Settings.Domain))
            return new AuthLoginResult(false, null, "Auth0 settings are missing.");

        var interval = TimeSpan.FromSeconds(Math.Max(3, deviceCode.Interval));

        while (DateTimeOffset.UtcNow < deviceCode.ExpiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = await Oauth.ExchangeDeviceCodeAsync(Settings, deviceCode.DeviceCode, cancellationToken);
            Logger.LogInformation("[Auth0] Device flow poll response. IsSuccess: {IsSuccess}, HasToken: {HasToken}",
                token.IsSuccess,
                !string.IsNullOrWhiteSpace(token.AccessToken));

            if (token.IsSuccess && !string.IsNullOrWhiteSpace(token.AccessToken))
            {
                if (!AcceptAccessToken(token.AccessToken))
                {
                    return new AuthLoginResult(false, null,
                        $"Authenticated but missing required role '{Settings.RequiredRole}'.");
                }

                await ApplyTokensAsync(token.AccessToken, token.ExpiresIn, token.RefreshToken);
                return new AuthLoginResult(true, AccessToken, null);
            }

            if (token.Error == "authorization_pending")
            {
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            if (token.Error == "slow_down")
            {
                interval += TimeSpan.FromSeconds(5);
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(token.Error))
                return new AuthLoginResult(false, null, token.ErrorDescription ?? token.Error);

            await Task.Delay(interval, cancellationToken);
        }

        Logger.LogWarning("[Auth0] Device code expired");
        return new AuthLoginResult(false, null, "Device code expired.");
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        await EnsureTokenReadyAsync(cancellationToken, forceRefresh);
        return HasValidToken ? AccessToken : null;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        await EnsureTokensLoadedAsync();
        return HasValidToken || !string.IsNullOrWhiteSpace(RefreshToken);
    }

    public virtual async Task LogoutAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            await ClearTokensCoreAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    protected bool IsExpired()
    {
        if (ExpiresAt == DateTimeOffset.MinValue) return true;
        var leeway = Settings.TokenLeewaySeconds;
        return ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(Math.Abs(leeway));
    }

    protected bool NeedsAccessTokenRefresh(bool forceRefresh)
        => forceRefresh
           || AuthTokenLifetime.ShouldRefreshAccessToken(
               ExpiresAt,
               DateTimeOffset.UtcNow,
               Settings.TokenLeewaySeconds != 0 ? Settings.TokenLeewaySeconds : AuthTokenLifetime.DefaultLeewaySeconds,
               LastRefreshedAt,
               Settings.SlidingRefreshAfterSeconds != 0
                   ? Settings.SlidingRefreshAfterSeconds
                   : AuthTokenLifetime.DefaultSlidingRefreshAfterSeconds);

    protected async Task EnsureTokensLoadedAsync()
    {
        if (TokenLoaded)
            return;

        await _sessionLock.WaitAsync();
        try
        {
            if (TokenLoaded)
                return;

            await LoadPersistedTokensAsync();
            TokenLoaded = true;
            if (LastRefreshedAt == DateTimeOffset.MinValue)
                LastRefreshedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    protected async Task EnsureTokenReadyAsync(CancellationToken cancellationToken, bool forceRefresh)
    {
        if (TokenLoaded
            && !forceRefresh
            && !NeedsAccessTokenRefresh(forceRefresh: false)
            && (HasValidToken || string.IsNullOrWhiteSpace(RefreshToken)))
        {
            return;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (!TokenLoaded)
            {
                await LoadPersistedTokensAsync();
                TokenLoaded = true;
                if (LastRefreshedAt == DateTimeOffset.MinValue)
                    LastRefreshedAt = DateTimeOffset.UtcNow;
            }

            if (string.IsNullOrWhiteSpace(RefreshToken) || !NeedsAccessTokenRefresh(forceRefresh))
                return;

            if (AuthTokenLifetime.ShouldRefreshInBackground(forceRefresh, HasValidToken, refreshDue: true))
            {
                ScheduleBackgroundSlidingRefresh();
                return;
            }

            var outcome = await RefreshAccessTokenAsync(cancellationToken);
            if (outcome == AuthTokenRefreshOutcome.Rejected)
                await ClearTokensCoreAsync();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    protected async Task ApplyTokensAsync(string accessToken, int expiresIn, string? refreshToken)
    {
        await _sessionLock.WaitAsync();
        try
        {
            await ApplyTokensCoreAsync(accessToken, expiresIn, refreshToken);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// PKCE code exchange + role gate + persist. Call this after the host browser wait so the
    /// session lock is not held across the loopback callback.
    /// </summary>
    protected async Task<AuthLoginResult> CompleteAuthorizationCodeAsync(
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        Auth0TokenHttpResult exchanged;
        try
        {
            exchanged = await Oauth.ExchangeAuthorizationCodeAsync(
                Settings, code, redirectUri, codeVerifier, cancellationToken);
        }
        catch (Exception ex)
        {
            return new AuthLoginResult(false, null, ex.Message);
        }

        if (!exchanged.IsSuccess || string.IsNullOrWhiteSpace(exchanged.AccessToken))
        {
            return new AuthLoginResult(false, null,
                exchanged.ErrorDescription ?? exchanged.Error ?? "Auth0 token exchange failed.");
        }

        if (!AcceptAccessToken(exchanged.AccessToken))
        {
            await LogoutAsync();
            return new AuthLoginResult(false, null,
                $"Authenticated but missing required role '{Settings.RequiredRole}'.");
        }

        await ApplyTokensAsync(
            exchanged.AccessToken,
            exchanged.ExpiresIn > 0 ? exchanged.ExpiresIn : 3600,
            exchanged.RefreshToken);
        return new AuthLoginResult(true, AccessToken, null);
    }

    private async Task ApplyTokensCoreAsync(string accessToken, int expiresIn, string? refreshToken)
    {
        AccessToken = accessToken;
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 3600);
        RefreshToken = AuthTokenLifetime.CoalesceRefreshToken(refreshToken, RefreshToken);
        LastRefreshedAt = DateTimeOffset.UtcNow;
        await PersistTokensAsync();
    }

    protected async Task ClearTokensCoreAsync()
    {
        AccessToken = null;
        RefreshToken = null;
        ExpiresAt = DateTimeOffset.MinValue;
        LastRefreshedAt = DateTimeOffset.MinValue;
        TokenLoaded = true;
        await ClearPersistedTokensAsync();
    }

    private void ScheduleBackgroundSlidingRefresh()
    {
        if (Interlocked.CompareExchange(ref _backgroundRefreshGate, 1, 0) != 0)
            return;

        _ = RefreshAccessTokenInBackgroundAsync();
    }

    private async Task RefreshAccessTokenInBackgroundAsync()
    {
        try
        {
            await _sessionLock.WaitAsync();
            try
            {
                if (string.IsNullOrWhiteSpace(RefreshToken) || !NeedsAccessTokenRefresh(forceRefresh: false))
                    return;

                var outcome = await RefreshAccessTokenAsync(CancellationToken.None);
                if (outcome == AuthTokenRefreshOutcome.Rejected)
                    await ClearTokensCoreAsync();
            }
            finally
            {
                _sessionLock.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0] Background sliding refresh failed");
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundRefreshGate, 0);
        }
    }

    protected async Task<AuthTokenRefreshOutcome> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(RefreshToken) ||
            string.IsNullOrWhiteSpace(Settings.ClientId) ||
            string.IsNullOrWhiteSpace(Settings.Domain))
        {
            return AuthTokenRefreshOutcome.TransientFailure;
        }

        try
        {
            var token = await Oauth.RefreshAsync(Settings, RefreshToken, cancellationToken);
            if (token.IsSuccess && !string.IsNullOrWhiteSpace(token.AccessToken))
            {
                await ApplyTokensCoreAsync(token.AccessToken, token.ExpiresIn, token.RefreshToken);
                return AuthTokenRefreshOutcome.Success;
            }

            Logger.LogWarning("[Auth0] Failed to refresh access token. Error: {Error}", token.Error);
            return AuthTokenLifetime.IsRefreshRejected(token.Error)
                ? AuthTokenRefreshOutcome.Rejected
                : AuthTokenRefreshOutcome.TransientFailure;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0] Refresh token request failed");
            return AuthTokenRefreshOutcome.TransientFailure;
        }
    }

    protected abstract Task LoadPersistedTokensAsync();

    protected abstract Task PersistTokensAsync();

    protected abstract Task ClearPersistedTokensAsync();
}
