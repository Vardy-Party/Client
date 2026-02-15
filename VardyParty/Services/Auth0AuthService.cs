using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Auth0.OidcClient;
using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
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

    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _accessToken;
    private Auth0Settings _auth0Settings = auth0Settings.Value;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private string? _refreshToken;
    private bool _tokenLoaded;

    public bool HasValidToken => !string.IsNullOrWhiteSpace(_accessToken) && !IsExpired(_auth0Settings);

    public async Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
    {
        if (_auth0Settings == null) return new AuthLoginResult(false, null, "Auth0 settings are missing.");

        if (HasValidToken) return new AuthLoginResult(true, _accessToken, null);

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (HasValidToken) return new AuthLoginResult(true, _accessToken, null);

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

            // AccessToken is already scoped to the audience since we included it in the scope during login
            var expiresIn = loginResult.AccessTokenExpiration != null
                ? (int)Math.Max(0, (loginResult.AccessTokenExpiration - DateTimeOffset.UtcNow).TotalSeconds)
                : 3600;

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
        if (_auth0Settings == null) return null;

        var clientId = _auth0Settings.ClientId;
        var domain = _auth0Settings.Domain;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(domain)) return null;

        var client = httpClientFactory.CreateClient();
        var endpoint = BuildAuth0Url(domain, "/oauth/device/code");
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId)
        };

        if (!string.IsNullOrWhiteSpace(_auth0Settings.Audience))
            form.Add(new KeyValuePair<string, string>("audience", _auth0Settings.Audience));

        if (!string.IsNullOrWhiteSpace(_auth0Settings.Scope))
            form.Add(new KeyValuePair<string, string>("scope", _auth0Settings.Scope));

        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken);
        if (payload == null || string.IsNullOrWhiteSpace(payload.DeviceCode) ||
            string.IsNullOrWhiteSpace(payload.UserCode) ||
            string.IsNullOrWhiteSpace(payload.VerificationUri)) return null;

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);
        var deviceCode = new AuthDeviceCode(
            payload.DeviceCode ?? string.Empty,
            payload.UserCode,
            payload.VerificationUri,
            payload.VerificationUriComplete,
            payload.ExpiresIn,
            payload.Interval <= 0 ? 5 : payload.Interval,
            expiresAt);

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

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTokenLoadedAsync(cancellationToken);
        return HasValidToken ? _accessToken : null;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        await EnsureTokenLoadedAsync(CancellationToken.None);
        return HasValidToken;
    }

    public async Task LogoutAsync()
    {
        await ClearTokensAsync();
        _auth0Settings = null;
    }

    private bool IsExpired(Auth0Settings? settings)
    {
        if (_expiresAt == DateTimeOffset.MinValue) return true;
        var leeway = settings?.TokenLeewaySeconds ?? 60;
        return _expiresAt <= DateTimeOffset.UtcNow.AddSeconds(Math.Abs(leeway));
    }

    private async Task EnsureTokenLoadedAsync(CancellationToken cancellationToken)
    {
        if (_tokenLoaded) return;

        try
        {
            _accessToken = await SecureStorage.Default.GetAsync(AccessTokenKey);
            _refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
            var expiresRaw = await SecureStorage.Default.GetAsync(ExpiresAtKey);
            if (long.TryParse(expiresRaw, out var unixSeconds))
                _expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Failed to load tokens from secure storage");
        }
        finally
        {
            _tokenLoaded = true;
        }

        if (!HasValidToken && !string.IsNullOrWhiteSpace(_refreshToken))
        {
            var refreshed = await RefreshAccessTokenAsync(cancellationToken);
            if (!refreshed) await ClearTokensAsync();
        }
    }

    private async Task SaveTokensAsync(string accessToken, int expiresIn, string? refreshToken)
    {
        _accessToken = accessToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 3600);
        _refreshToken = refreshToken;

        try
        {
            await SecureStorage.Default.SetAsync(AccessTokenKey, _accessToken);
            await SecureStorage.Default.SetAsync(ExpiresAtKey, _expiresAt.ToUnixTimeSeconds().ToString());
            if (!string.IsNullOrWhiteSpace(_refreshToken))
                await SecureStorage.Default.SetAsync(RefreshTokenKey, _refreshToken);
            else
                SecureStorage.Default.Remove(RefreshTokenKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Failed to persist tokens to secure storage");
        }
    }

    private Task ClearTokensAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = DateTimeOffset.MinValue;
        _tokenLoaded = true;

        try
        {
            SecureStorage.Default.Remove(AccessTokenKey);
            SecureStorage.Default.Remove(RefreshTokenKey);
            SecureStorage.Default.Remove(ExpiresAtKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Failed to clear tokens from secure storage");
        }

        return Task.CompletedTask;
    }

    private async Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_auth0Settings == null || string.IsNullOrWhiteSpace(_refreshToken)) return false;

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
                await SaveTokensAsync(tokenPayload.AccessToken, tokenPayload.ExpiresIn,
                    tokenPayload.RefreshToken ?? _refreshToken);
                return true;
            }

            logger.LogWarning("[Auth0] Failed to refresh access token. Error: {Error}", tokenPayload?.Error);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth0] Refresh token request failed");
            return false;
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
            Scope = settings.Scope
        };

        // Use a SocketsHttpHandler with an IPv4-preferring ConnectCallback.
        // On some Android devices, DNS returns AAAA (IPv6) records first but
        // IPv6 routing is broken, causing both AndroidMessageHandler (platform
        // OkHttp) and the default SocketsHttpHandler to time out or get
        // "Socket closed".  Explicitly resolving DNS and connecting via IPv4
        // bypasses the broken IPv6 path.
        options.BackchannelHandler = CreateFallbackHandler();

        var client = new Auth0Client(options);
        return client;
    }

    /// <summary>
    ///     Creates a <see cref="SocketsHttpHandler" /> that tries each DNS address
    ///     in order (typically IPv6 first) with a short per-address timeout,
    ///     falling back to the next address when a connection fails.
    /// </summary>
    private static SocketsHttpHandler CreateFallbackHandler()
    {
        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);

                if (entry.AddressList.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);

                // Try each address in DNS order; if the first (often IPv6)
                // fails within 5s, fall through to the next (often IPv4).
                Exception? last = null;
                foreach (var address in entry.AddressList)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(5));
                        await socket.ConnectAsync(
                            new IPEndPoint(address, context.DnsEndPoint.Port),
                            cts.Token);
                        return new NetworkStream(socket, true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        last = ex;
                    }
                }

                throw last ?? new SocketException((int)SocketError.HostUnreachable);
            }
        };
    }

    private static string BuildAuth0Url(string domain, string path)
    {
        var normalized = domain.Trim();
        normalized = normalized.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.TrimEnd('/');
        return $"https://{normalized}{path}";
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