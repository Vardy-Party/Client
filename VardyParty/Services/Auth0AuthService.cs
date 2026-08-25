using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Auth0.OidcClient;
using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Hosting;
using VardyParty.Providers;

namespace VardyParty.Services;

public class Auth0AuthService(
    ILogger<Auth0AuthService> logger,
    IOptions<Auth0Settings> auth0Settings,
    IHttpClientFactory httpClientFactory) : IAuthTokenProvider, IAuthLoginService
{
    private const string AccessTokenKey = "Auth0.AccessToken";
    private const string RefreshTokenKey = "Auth0.RefreshToken";
    private const string ExpiresAtKey = "Auth0.ExpiresAt";
    private const string LastRefreshedAtKey = "Auth0.LastRefreshedAt";

    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _accessToken;
    private Auth0Settings _auth0Settings = auth0Settings.Value;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefreshedAt = DateTimeOffset.MinValue;
    private string? _refreshToken;
    private bool _tokenLoaded;

    public bool HasValidToken => !string.IsNullOrWhiteSpace(_accessToken) && !IsExpired(_auth0Settings);

    public async Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
    {
        if (_auth0Settings == null) return new AuthLoginResult(false, null, "Auth0 settings are missing.");

        await EnsureTokenReadyAsync(cancellationToken, forceRefresh: false);
        if (HasValidToken) return new AuthLoginResult(true, _accessToken, null);

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (HasValidToken) return new AuthLoginResult(true, _accessToken, null);

            if (!MauiProgram.IsWindowsPackaged)
            {
                logger.LogWarning("[Auth0] Interactive login requires a packaged Windows app.");
                return new AuthLoginResult(false, null,
                    "Interactive login requires a packaged Windows app. Use device sign-in instead.");
            }

            var client = BuildAuth0Client(_auth0Settings);
            logger.LogInformation("[Auth0] Starting login...");

            LoginResult loginResult;
            try
            {
                logger.LogInformation("[Auth0] Calling LoginAsync with audience: {Audience}", _auth0Settings.Audience);
                loginResult = await client.LoginAsync(new { audience = _auth0Settings.Audience }, cancellationToken);
                logger.LogInformation("[Auth0] LoginAsync returned. IsError: {IsError}, Error: {Error}",
                    loginResult.IsError, loginResult.Error);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                logger.LogError(ex,
                    "[Auth0] Login timed out. This usually indicates network connectivity issues or DNS resolution problems.");
                return new AuthLoginResult(false, null, "Connection timed out. Please check your network connection.");
            }
            catch (Exception ex) when (ex.Message.Contains("SocketException") || ex.Message.Contains("Socket closed"))
            {
                logger.LogError(ex, "[Auth0] Network error during login. Socket closed or connection failed.");
                return new AuthLoginResult(false, null, "Network error. Please check your internet connection.");
            }

            if (loginResult.IsError)
            {
                logger.LogWarning("[Auth0] Login error: {Error}", loginResult.Error);
                return new AuthLoginResult(false, null, loginResult.Error);
            }

            logger.LogInformation("[Auth0] Login successful. User: {User}, AccessToken present: {HasToken}",
                loginResult.User?.Identity?.Name ?? "unknown",
                !string.IsNullOrWhiteSpace(loginResult.AccessToken));

            // Check for required role claim on authenticated user
            var roleClaims = loginResult.User?.FindAll(_auth0Settings.RequiredRoleClaimType)?.ToList();
            logger.LogInformation("[Auth0] Found {ClaimCount} role claims of type {ClaimType}",
                roleClaims?.Count ?? 0,
                _auth0Settings.RequiredRoleClaimType);

            if (roleClaims == null || !roleClaims.Any())
            {
                logger.LogWarning("[Auth0] User has no role claims of type {ClaimType}",
                    _auth0Settings.RequiredRoleClaimType);
                return new AuthLoginResult(false, null, null);
            }

            var hasRequiredRole = roleClaims.Any(c =>
                !string.IsNullOrWhiteSpace(c.Value) && c.Value.Split(' ').Contains(_auth0Settings.RequiredRole));
            logger.LogInformation("[Auth0] User has required role '{Role}': {HasRole}", _auth0Settings.RequiredRole,
                hasRequiredRole);

            if (!hasRequiredRole)
            {
                logger.LogWarning("[Auth0] User authenticated but missing required role: {RequiredRole}",
                    _auth0Settings.RequiredRole);
                return new AuthLoginResult(false, null, null);
            }

            // AccessTokenExpiration is DateTimeOffset (never null); default means the IdP omitted expiry.
            var expiration = loginResult.AccessTokenExpiration;
            var expiresIn = expiration == default
                ? 3600
                : (int)Math.Max(0, (expiration - DateTimeOffset.UtcNow).TotalSeconds);

            logger.LogInformation("[Auth0] Setting token with {ExpiresIn}s expiry", expiresIn);
            await SaveTokensAsync(loginResult.AccessToken, expiresIn, loginResult.RefreshToken);
            logger.LogInformation("[Auth0] Token set successfully");
            return new AuthLoginResult(true, _accessToken, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Auth0] Interactive login failed");
            return new AuthLoginResult(false, null, ex.Message);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<AuthDeviceLoginResult?> StartDeviceLoginAsync(CancellationToken cancellationToken = default)
    {
        if (_auth0Settings == null)
        {
            logger.LogWarning("[Auth0] Device login missing Auth0 settings instance");
            throw new InvalidOperationException("Auth0 is not configured on this device build.");
        }

        var clientId = _auth0Settings.ClientId;
        var domain = _auth0Settings.Domain;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(domain))
        {
            logger.LogWarning("[Auth0] Device login missing ClientId/Domain");
            throw new InvalidOperationException("Auth0 is not configured on this device build.");
        }

        var client = httpClientFactory.CreateClient();
        var endpoint = BuildAuth0Url(domain, "/oauth/device/code");
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId)
        };

        if (!string.IsNullOrWhiteSpace(_auth0Settings.Audience))
            form.Add(new KeyValuePair<string, string>("audience", _auth0Settings.Audience));

        form.Add(new KeyValuePair<string, string>("scope", AuthTokenLifetime.EnsureOfflineAccess(_auth0Settings.Scope)));

        logger.LogInformation("[Auth0] Requesting device code from {Endpoint}", endpoint);
        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var authError = TryReadOAuthError(body);
            logger.LogWarning(
                "[Auth0] Device code request failed: {Status} {Error} {Description} body={Body}",
                (int)response.StatusCode,
                authError?.Error,
                authError?.ErrorDescription,
                body.Length > 300 ? body[..300] : body);

            var message = !string.IsNullOrWhiteSpace(authError?.ErrorDescription)
                ? authError.ErrorDescription
                : !string.IsNullOrWhiteSpace(authError?.Error)
                    ? authError.Error
                    : $"Sign-in failed ({(int)response.StatusCode}). Check Auth0 device-code grant.";
            throw new InvalidOperationException(message);
        }

        var payload = System.Text.Json.JsonSerializer.Deserialize<DeviceCodeResponse>(body);
        if (payload == null || string.IsNullOrWhiteSpace(payload.DeviceCode) ||
            string.IsNullOrWhiteSpace(payload.UserCode) ||
            string.IsNullOrWhiteSpace(payload.VerificationUri))
        {
            logger.LogWarning("[Auth0] Device code response missing required fields: {Body}",
                body.Length > 300 ? body[..300] : body);
            throw new InvalidOperationException("Auth0 device sign-in returned an incomplete response.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);
        var deviceCode = new AuthDeviceCode(
            payload.DeviceCode ?? string.Empty,
            payload.UserCode,
            payload.VerificationUri,
            payload.VerificationUriComplete,
            payload.ExpiresIn,
            payload.Interval <= 0 ? 5 : payload.Interval,
            expiresAt);

        logger.LogInformation("[Auth0] Device code issued. UserCode={UserCode}", deviceCode.UserCode);
        return new AuthDeviceLoginResult(deviceCode);
    }

    public async Task<AuthLoginResult> PollDeviceLoginAsync(AuthDeviceCode deviceCode,
        CancellationToken cancellationToken = default)
    {
        if (_auth0Settings == null) return new AuthLoginResult(false, null, "Auth0 settings are missing.");

        var clientId = _auth0Settings.ClientId;
        var domain = _auth0Settings.Domain;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(domain))
            return new AuthLoginResult(false, null, "Auth0 settings are missing.");

        var interval = TimeSpan.FromSeconds(Math.Max(3, deviceCode.Interval));
        var client = httpClientFactory.CreateClient();
        var endpoint = BuildAuth0Url(domain, "/oauth/token");

        while (DateTimeOffset.UtcNow < deviceCode.ExpiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(
                new Dictionary<string, string?>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode.DeviceCode,
                    ["client_id"] = clientId,
                    ["audience"] = _auth0Settings.Audience
                }!), cancellationToken);

            var tokenPayload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            logger.LogInformation("[Auth0] Device flow poll response. IsSuccess: {IsSuccess}, HasToken: {HasToken}",
                response.IsSuccessStatusCode,
                !string.IsNullOrWhiteSpace(tokenPayload?.AccessToken));

            if (response.IsSuccessStatusCode && tokenPayload != null &&
                !string.IsNullOrWhiteSpace(tokenPayload.AccessToken))
            {
                logger.LogInformation("[Auth0] Device flow: Token received. Setting token with expiry");
                await SaveTokensAsync(tokenPayload.AccessToken, tokenPayload.ExpiresIn, tokenPayload.RefreshToken);
                logger.LogInformation("[Auth0] Device flow: Token set successfully");
                return new AuthLoginResult(true, _accessToken, null);
            }

            if (tokenPayload?.Error == "authorization_pending")
            {
                logger.LogInformation("[Auth0] Device flow: Authorization pending, retrying in {Interval}s",
                    interval.TotalSeconds);
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            if (tokenPayload?.Error == "slow_down")
            {
                interval = interval + TimeSpan.FromSeconds(5);
                logger.LogInformation("[Auth0] Device flow: Slow down requested, new interval: {Interval}s",
                    interval.TotalSeconds);
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tokenPayload?.Error))
            {
                logger.LogWarning("[Auth0] Device flow error: {Error}", tokenPayload.Error);
                return new AuthLoginResult(false, null, tokenPayload.ErrorDescription ?? tokenPayload.Error);
            }

            await Task.Delay(interval, cancellationToken);
        }

        logger.LogWarning("[Auth0] Device code expired");
        return new AuthLoginResult(false, null, "Device code expired.");
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        await EnsureTokenReadyAsync(cancellationToken, forceRefresh);
        return HasValidToken ? _accessToken : null;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        await EnsureTokenReadyAsync(CancellationToken.None, forceRefresh: false);
        return HasValidToken || !string.IsNullOrWhiteSpace(_refreshToken);
    }

    public async Task LogoutAsync()
    {
        await _loginLock.WaitAsync();
        try
        {
            await ClearTokensCoreAsync();
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private bool IsExpired(Auth0Settings? settings)
    {
        if (_expiresAt == DateTimeOffset.MinValue) return true;
        var leeway = settings?.TokenLeewaySeconds ?? 60;
        return _expiresAt <= DateTimeOffset.UtcNow.AddSeconds(Math.Abs(leeway));
    }

    private bool NeedsAccessTokenRefresh(bool forceRefresh)
        => forceRefresh
           || AuthTokenLifetime.ShouldRefreshAccessToken(
               _expiresAt,
               DateTimeOffset.UtcNow,
               _auth0Settings?.TokenLeewaySeconds ?? AuthTokenLifetime.DefaultLeewaySeconds,
               _lastRefreshedAt,
               _auth0Settings?.SlidingRefreshAfterSeconds ?? AuthTokenLifetime.DefaultSlidingRefreshAfterSeconds);

    private async Task EnsureTokenReadyAsync(CancellationToken cancellationToken, bool forceRefresh)
    {
        if (_tokenLoaded
            && !forceRefresh
            && !NeedsAccessTokenRefresh(forceRefresh: false)
            && (HasValidToken || string.IsNullOrWhiteSpace(_refreshToken)))
        {
            return;
        }

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (!_tokenLoaded)
            {
                await LoadTokensFromSecureStorageAsync();
                _tokenLoaded = true;
                if (_lastRefreshedAt == DateTimeOffset.MinValue)
                    _lastRefreshedAt = DateTimeOffset.UtcNow;
            }

            if (string.IsNullOrWhiteSpace(_refreshToken) || !NeedsAccessTokenRefresh(forceRefresh))
                return;

            var outcome = await RefreshAccessTokenAsync(cancellationToken);
            if (outcome == AuthTokenRefreshOutcome.Rejected)
                await ClearTokensCoreAsync();
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task LoadTokensFromSecureStorageAsync()
    {
        try
        {
            _accessToken = await SecureStorage.Default.GetAsync(AccessTokenKey);
            _refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
            var expiresRaw = await SecureStorage.Default.GetAsync(ExpiresAtKey);
            if (long.TryParse(expiresRaw, out var unixSeconds))
                _expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

            var lastRefreshedRaw = await SecureStorage.Default.GetAsync(LastRefreshedAtKey);
            if (long.TryParse(lastRefreshedRaw, out var lastRefreshedSeconds))
                _lastRefreshedAt = DateTimeOffset.FromUnixTimeSeconds(lastRefreshedSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Failed to load tokens from secure storage");
        }
    }

    private async Task SaveTokensAsync(string accessToken, int expiresIn, string? refreshToken)
    {
        _accessToken = accessToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 3600);
        _refreshToken = AuthTokenLifetime.CoalesceRefreshToken(refreshToken, _refreshToken);
        _lastRefreshedAt = DateTimeOffset.UtcNow;

        try
        {
            await SecureStorage.Default.SetAsync(AccessTokenKey, _accessToken);
            await SecureStorage.Default.SetAsync(ExpiresAtKey, _expiresAt.ToUnixTimeSeconds().ToString());
            await SecureStorage.Default.SetAsync(LastRefreshedAtKey, _lastRefreshedAt.ToUnixTimeSeconds().ToString());
            if (!string.IsNullOrWhiteSpace(_refreshToken))
                await SecureStorage.Default.SetAsync(RefreshTokenKey, _refreshToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Failed to persist tokens to secure storage");
        }
    }

    private Task ClearTokensCoreAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = DateTimeOffset.MinValue;
        _lastRefreshedAt = DateTimeOffset.MinValue;
        _tokenLoaded = true;

        try
        {
            SecureStorage.Default.Remove(AccessTokenKey);
            SecureStorage.Default.Remove(RefreshTokenKey);
            SecureStorage.Default.Remove(ExpiresAtKey);
            SecureStorage.Default.Remove(LastRefreshedAtKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Failed to clear tokens from secure storage");
        }

        return Task.CompletedTask;
    }

    private async Task<AuthTokenRefreshOutcome> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_auth0Settings == null || string.IsNullOrWhiteSpace(_refreshToken))
            return AuthTokenRefreshOutcome.TransientFailure;

        try
        {
            var client = httpClientFactory.CreateClient();
            var endpoint = BuildAuth0Url(_auth0Settings.Domain, "/oauth/token");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = _auth0Settings.ClientId,
                ["audience"] = _auth0Settings.Audience
            }!), cts.Token);

            var tokenPayload = await response.Content.ReadFromJsonAsync<TokenResponse>(cts.Token);
            if (response.IsSuccessStatusCode && tokenPayload != null &&
                !string.IsNullOrWhiteSpace(tokenPayload.AccessToken))
            {
                await SaveTokensAsync(tokenPayload.AccessToken, tokenPayload.ExpiresIn, tokenPayload.RefreshToken);
                return AuthTokenRefreshOutcome.Success;
            }

            logger.LogWarning("[Auth0] Failed to refresh access token. Error: {Error}", tokenPayload?.Error);
            return AuthTokenLifetime.IsRefreshRejected(tokenPayload?.Error)
                ? AuthTokenRefreshOutcome.Rejected
                : AuthTokenRefreshOutcome.TransientFailure;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Refresh token request failed");
            return AuthTokenRefreshOutcome.TransientFailure;
        }
    }

    private Auth0Client BuildAuth0Client(Auth0Settings settings)
    {
        var domain = settings.Domain;
        var expectedDiscoveryUrl = $"https://{domain}/.well-known/openid-configuration";
        logger.LogInformation("[Auth0] Building client for domain: {Domain}", domain);
        logger.LogInformation("[Auth0] Expected discovery URL: {Url}", expectedDiscoveryUrl);

        var options = new Auth0ClientOptions
        {
            Domain = domain,
            ClientId = settings.ClientId,
            RedirectUri = settings.RedirectUri,
            PostLogoutRedirectUri = settings.PostLogoutRedirectUri,
            Scope = AuthTokenLifetime.EnsureOfflineAccess(settings.Scope)
        };

        // Same IPv4-first sockets handler as catalog HTTP. On some Android
        // devices DNS returns AAAA first but IPv6 routing is broken, so OkHttp
        // and default SocketsHttpHandler hang or get "Socket closed".
        options.BackchannelHandler = Ipv4PreferringSocketsHttpHandler.Create();

        var client = new Auth0Client(options);
        return client;
    }

    private static OAuthErrorResponse? TryReadOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<OAuthErrorResponse>(body);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildAuth0Url(string domain, string path)
    {
        var normalized = domain.Trim();
        normalized = normalized.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.TrimEnd('/');
        return $"https://{normalized}{path}";
    }


    private sealed class OAuthErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; init; }

        [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }

        [JsonPropertyName("user_code")] public string? UserCode { get; init; }

        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; init; }

        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

        [JsonPropertyName("interval")] public int Interval { get; init; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }

        [JsonPropertyName("token_type")] public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

        [JsonPropertyName("error")] public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}