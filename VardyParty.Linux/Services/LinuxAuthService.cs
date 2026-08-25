using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Providers;
using VardyParty.Services;

namespace VardyParty.Linux.Services;

public class LinuxAuthService : IAuthTokenProvider, IAuthLoginService
{
    private readonly ILogger<LinuxAuthService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly string _tokenFilePath;
    private readonly string _tokenKeyPath;
    private Auth0Settings _auth0Settings;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefreshedAt = DateTimeOffset.MinValue;
    private bool _tokenLoaded;

    public LinuxAuthService(
        ILogger<LinuxAuthService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<Auth0Settings> auth0Settings)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _auth0Settings = auth0Settings.Value;

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VardyParty");
        _tokenFilePath = Path.Combine(configDir, "auth0-tokens.bin");
        _tokenKeyPath = Path.Combine(configDir, ".token-key");
    }

    public bool HasValidToken => !string.IsNullOrWhiteSpace(_accessToken) && !IsExpired(_auth0Settings);

    public async Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTokenReadyAsync(cancellationToken, forceRefresh: false);
        if (HasValidToken)
        {
            return new AuthLoginResult(true, _accessToken, null);
        }

        if (!TryGetLoopbackRedirectUri(out var redirectUri))
        {
            _logger.LogWarning("[Auth0/Linux] RedirectUri is not loopback; falling back to device login");
            var deviceLogin = await StartDeviceLoginAsync(cancellationToken);
            if (deviceLogin?.DeviceCode == null)
            {
                return new AuthLoginResult(false, null,
                    "Unable to start interactive login (invalid redirect URI) or device login.");
            }

            _logger.LogInformation("[Auth0/Linux] Complete sign-in at {Uri} with code {Code}",
                deviceLogin.DeviceCode.VerificationUri, deviceLogin.DeviceCode.UserCode);
            return await PollDeviceLoginAsync(deviceLogin.DeviceCode, cancellationToken);
        }

        var state = CreateRandomBase64Url(32);
        var codeVerifier = CreateRandomBase64Url(64);
        var codeChallenge = CreateCodeChallenge(codeVerifier);

        var authUrl = BuildAuthorizeUrl(redirectUri, state, codeChallenge);

        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(BuildListenerPrefix(redirectUri));
            listener.Start();

            if (!OpenBrowser(authUrl))
            {
                listener.Stop();
                return new AuthLoginResult(false, null,
                    "Could not open browser for Auth0 login. Please install xdg-open/gio or use device login.");
            }

            var callback = await WaitForCallbackAsync(listener, TimeSpan.FromMinutes(3), cancellationToken);
            if (callback == null)
            {
                return new AuthLoginResult(false, null, "Timed out waiting for Auth0 login callback.");
            }

            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                return new AuthLoginResult(false, null, callback.ErrorDescription ?? callback.Error);
            }

            if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            {
                return new AuthLoginResult(false, null, "Auth0 callback state mismatch.");
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                return new AuthLoginResult(false, null, "Auth0 callback did not include authorization code.");
            }

            var exchanged = await ExchangeAuthorizationCodeAsync(callback.Code, redirectUri.ToString(), codeVerifier,
                cancellationToken);
            if (!exchanged.IsSuccess)
            {
                return new AuthLoginResult(false, null, exchanged.Error ?? "Auth0 token exchange failed.");
            }

            if (!HasRequiredRole(exchanged.AccessToken!))
            {
                await ClearTokensAsync();
                return new AuthLoginResult(false, null,
                    $"Authenticated but missing required role '{_auth0Settings.RequiredRole}'.");
            }

            await SaveTokensAsync(exchanged.AccessToken!, exchanged.ExpiresIn, exchanged.RefreshToken);
            return new AuthLoginResult(true, _accessToken, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Auth0/Linux] Interactive login failed");
            return new AuthLoginResult(false, null, ex.Message);
        }
    }

    public async Task<AuthDeviceLoginResult?> StartDeviceLoginAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_auth0Settings.ClientId) || string.IsNullOrWhiteSpace(_auth0Settings.Domain))
        {
            _logger.LogWarning("[Auth0/Linux] Missing Auth0 settings for device login");
            return null;
        }

        var client = _httpClientFactory.CreateClient(Auth0HttpClients.Name);
        var endpoint = BuildAuth0Url(_auth0Settings.Domain, "/oauth/device/code");
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", _auth0Settings.ClientId)
        };

        if (!string.IsNullOrWhiteSpace(_auth0Settings.Audience))
        {
            form.Add(new KeyValuePair<string, string>("audience", _auth0Settings.Audience));
        }

        form.Add(new KeyValuePair<string, string>("scope", AuthTokenLifetime.EnsureOfflineAccess(_auth0Settings.Scope)));

        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken);
        if (payload == null || string.IsNullOrWhiteSpace(payload.DeviceCode) ||
            string.IsNullOrWhiteSpace(payload.UserCode) || string.IsNullOrWhiteSpace(payload.VerificationUri))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);
        return new AuthDeviceLoginResult(new AuthDeviceCode(
            payload.DeviceCode,
            payload.UserCode,
            payload.VerificationUri,
            payload.VerificationUriComplete,
            payload.ExpiresIn,
            payload.Interval <= 0 ? 5 : payload.Interval,
            expiresAt));
    }

    public async Task<AuthLoginResult> PollDeviceLoginAsync(AuthDeviceCode deviceCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_auth0Settings.ClientId) || string.IsNullOrWhiteSpace(_auth0Settings.Domain))
        {
            return new AuthLoginResult(false, null, "Auth0 settings are missing.");
        }

        var interval = TimeSpan.FromSeconds(Math.Max(3, deviceCode.Interval));
        var client = _httpClientFactory.CreateClient(Auth0HttpClients.Name);
        var endpoint = BuildAuth0Url(_auth0Settings.Domain, "/oauth/token");

        while (DateTimeOffset.UtcNow < deviceCode.ExpiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(
                new Dictionary<string, string?>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode.DeviceCode,
                    ["client_id"] = _auth0Settings.ClientId,
                    ["audience"] = _auth0Settings.Audience
                }!), cancellationToken);

            var tokenPayload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (response.IsSuccessStatusCode && tokenPayload != null && !string.IsNullOrWhiteSpace(tokenPayload.AccessToken))
            {
                if (!HasRequiredRole(tokenPayload.AccessToken))
                {
                    return new AuthLoginResult(false, null,
                        $"Authenticated but missing required role '{_auth0Settings.RequiredRole}'.");
                }

                await SaveTokensAsync(tokenPayload.AccessToken, tokenPayload.ExpiresIn, tokenPayload.RefreshToken);
                return new AuthLoginResult(true, _accessToken, null);
            }

            if (tokenPayload?.Error == "authorization_pending")
            {
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            if (tokenPayload?.Error == "slow_down")
            {
                interval = interval + TimeSpan.FromSeconds(5);
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tokenPayload?.Error))
            {
                return new AuthLoginResult(false, null, tokenPayload.ErrorDescription ?? tokenPayload.Error);
            }

            await Task.Delay(interval, cancellationToken);
        }

        return new AuthLoginResult(false, null, "Device code expired.");
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        await EnsureTokenReadyAsync(cancellationToken, forceRefresh);
        return HasValidToken ? _accessToken : null;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        await EnsureTokensLoadedAsync();
        return HasValidToken || !string.IsNullOrWhiteSpace(_refreshToken);
    }

    public async Task LogoutAsync()
    {
        await ClearTokensAsync();
        _logger.LogInformation("[Auth0/Linux] Logged out and cleared local tokens");
    }

    private bool IsExpired(Auth0Settings? settings)
    {
        if (_expiresAt == DateTimeOffset.MinValue)
        {
            return true;
        }

        var leeway = settings?.TokenLeewaySeconds ?? 60;
        return _expiresAt <= DateTimeOffset.UtcNow.AddSeconds(Math.Abs(leeway));
    }

    private bool NeedsAccessTokenRefresh(bool forceRefresh)
        => forceRefresh
           || AuthTokenLifetime.ShouldRefreshAccessToken(
               _expiresAt,
               DateTimeOffset.UtcNow,
               _auth0Settings.TokenLeewaySeconds,
               _lastRefreshedAt,
               _auth0Settings.SlidingRefreshAfterSeconds);

    private async Task EnsureTokensLoadedAsync()
    {
        if (_tokenLoaded)
        {
            return;
        }

        await _authLock.WaitAsync();
        try
        {
            if (_tokenLoaded)
            {
                return;
            }

            await LoadTokensFromDiskAsync(CancellationToken.None);
            _tokenLoaded = true;
            if (_lastRefreshedAt == DateTimeOffset.MinValue)
            {
                _lastRefreshedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task EnsureTokenReadyAsync(CancellationToken cancellationToken, bool forceRefresh)
    {
        if (_tokenLoaded
            && !forceRefresh
            && !NeedsAccessTokenRefresh(forceRefresh: false)
            && (HasValidToken || string.IsNullOrWhiteSpace(_refreshToken)))
        {
            return;
        }

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (!_tokenLoaded)
            {
                await LoadTokensFromDiskAsync(cancellationToken);
                _tokenLoaded = true;
                if (_lastRefreshedAt == DateTimeOffset.MinValue)
                {
                    _lastRefreshedAt = DateTimeOffset.UtcNow;
                }
            }

            if (string.IsNullOrWhiteSpace(_refreshToken) || !NeedsAccessTokenRefresh(forceRefresh))
            {
                return;
            }

            if (!AuthTokenLifetime.MustRefreshBeforeUse(forceRefresh, HasValidToken))
            {
                return;
            }

            var outcome = await RefreshAccessTokenAsync(cancellationToken);
            if (outcome == AuthTokenRefreshOutcome.Rejected)
            {
                await ClearTokensCoreAsync();
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task LoadTokensFromDiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await TryReadTokenJsonAsync(cancellationToken);
            if (json == null)
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<StoredTokenPayload>(json);
            if (payload == null)
            {
                return;
            }

            _accessToken = payload.AccessToken;
            _refreshToken = payload.RefreshToken;
            _expiresAt = payload.ExpiresAtUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds)
                : DateTimeOffset.MinValue;
            _lastRefreshedAt = payload.LastRefreshedAtUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(payload.LastRefreshedAtUnixSeconds)
                : DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Auth0/Linux] Failed to load token cache from disk");
        }
    }

    private async Task<string?> TryReadTokenJsonAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_tokenFilePath))
        {
            var bytes = await File.ReadAllBytesAsync(_tokenFilePath, cancellationToken);
            return UnprotectTokens(bytes);
        }

        var legacyPath = Path.Combine(
            Path.GetDirectoryName(_tokenFilePath) ?? string.Empty,
            "auth0-tokens.json");
        if (!File.Exists(legacyPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(legacyPath, cancellationToken);
        try
        {
            await SaveEncryptedJsonAsync(json);
            File.Delete(legacyPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Auth0/Linux] Failed to migrate plaintext token cache");
        }

        return json;
    }

    private async Task SaveTokensAsync(string accessToken, int expiresIn, string? refreshToken)
    {
        _accessToken = accessToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 3600);
        _refreshToken = AuthTokenLifetime.CoalesceRefreshToken(refreshToken, _refreshToken);
        _lastRefreshedAt = DateTimeOffset.UtcNow;

        try
        {
            var payload = new StoredTokenPayload
            {
                AccessToken = _accessToken,
                RefreshToken = _refreshToken,
                ExpiresAtUnixSeconds = _expiresAt.ToUnixTimeSeconds(),
                LastRefreshedAtUnixSeconds = _lastRefreshedAt.ToUnixTimeSeconds()
            };

            var json = JsonSerializer.Serialize(payload);
            await SaveEncryptedJsonAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Auth0/Linux] Failed to persist token cache to disk");
        }
    }

    private async Task SaveEncryptedJsonAsync(string json)
    {
        var directory = Path.GetDirectoryName(_tokenFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = ProtectTokens(json);
        await File.WriteAllBytesAsync(_tokenFilePath, bytes);
        RestrictUserOnly(_tokenFilePath);
        RestrictUserOnly(_tokenKeyPath);
    }

    private byte[] ProtectTokens(string plaintext)
    {
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);

        var output = new byte[3 + nonce.Length + tag.Length + cipher.Length];
        output[0] = (byte)'V';
        output[1] = (byte)'P';
        output[2] = (byte)'1';
        Buffer.BlockCopy(nonce, 0, output, 3, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, 15, tag.Length);
        Buffer.BlockCopy(cipher, 0, output, 31, cipher.Length);
        return output;
    }

    private string UnprotectTokens(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == (byte)'V' && bytes[1] == (byte)'P' && bytes[2] == (byte)'1')
        {
            var key = GetOrCreateKey();
            var nonce = bytes.AsSpan(3, 12);
            var tag = bytes.AsSpan(15, 16);
            var cipher = bytes.AsSpan(31);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private byte[] GetOrCreateKey()
    {
        var directory = Path.GetDirectoryName(_tokenKeyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_tokenKeyPath))
        {
            var existing = File.ReadAllBytes(_tokenKeyPath);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_tokenKeyPath, key);
        RestrictUserOnly(_tokenKeyPath);
        return key;
    }

    private static void RestrictUserOnly(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task ClearTokensAsync()
    {
        await _authLock.WaitAsync();
        try
        {
            await ClearTokensCoreAsync();
        }
        finally
        {
            _authLock.Release();
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
            if (File.Exists(_tokenFilePath))
            {
                File.Delete(_tokenFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Auth0/Linux] Failed to delete token cache file");
        }

        return Task.CompletedTask;
    }

    private async Task<AuthTokenRefreshOutcome> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken) || string.IsNullOrWhiteSpace(_auth0Settings.ClientId) ||
            string.IsNullOrWhiteSpace(_auth0Settings.Domain))
        {
            return AuthTokenRefreshOutcome.TransientFailure;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(Auth0HttpClients.Name);
            var endpoint = BuildAuth0Url(_auth0Settings.Domain, "/oauth/token");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = _auth0Settings.ClientId,
                ["audience"] = _auth0Settings.Audience
            }!), cts.Token);

            var tokenPayload = await response.Content.ReadFromJsonAsync<TokenResponse>(cts.Token);
            if (response.IsSuccessStatusCode && tokenPayload != null && !string.IsNullOrWhiteSpace(tokenPayload.AccessToken))
            {
                await SaveTokensAsync(tokenPayload.AccessToken, tokenPayload.ExpiresIn, tokenPayload.RefreshToken);
                return AuthTokenRefreshOutcome.Success;
            }

            _logger.LogWarning("[Auth0/Linux] Failed to refresh token. Error: {Error}", tokenPayload?.Error);
            return AuthTokenLifetime.IsRefreshRejected(tokenPayload?.Error)
                ? AuthTokenRefreshOutcome.Rejected
                : AuthTokenRefreshOutcome.TransientFailure;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Auth0/Linux] Refresh token request failed");
            return AuthTokenRefreshOutcome.TransientFailure;
        }
    }

    private static string BuildAuth0Url(string domain, string path)
    {
        var normalized = domain.Trim();
        normalized = normalized.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.TrimEnd('/');
        return $"https://{normalized}{path}";
    }

    private string BuildAuthorizeUrl(Uri redirectUri, string state, string codeChallenge)
    {
        var authEndpoint = BuildAuth0Url(_auth0Settings.Domain, "/authorize");
        var scope = AuthTokenLifetime.EnsureOfflineAccess(_auth0Settings.Scope);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _auth0Settings.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = scope,
            ["audience"] = _auth0Settings.Audience,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var queryString = string.Join("&", query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return $"{authEndpoint}?{queryString}";
    }

    private bool TryGetLoopbackRedirectUri(out Uri uri)
    {
        if (Uri.TryCreate(_auth0Settings.RedirectUri, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            (parsed.IsLoopback || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string BuildListenerPrefix(Uri redirectUri)
    {
        var path = string.IsNullOrWhiteSpace(redirectUri.AbsolutePath) || redirectUri.AbsolutePath == "/"
            ? "/"
            : redirectUri.AbsolutePath.TrimEnd('/') + "/";
        var port = redirectUri.IsDefaultPort ? 80 : redirectUri.Port;
        return $"{redirectUri.Scheme}://{redirectUri.Host}:{port}{path}";
    }

    private static string CreateRandomBase64Url(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    private bool OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", url)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("gio", $"open {url}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Auth0/Linux] Failed to open browser for interactive login");
                return false;
            }
        }
    }

    private async Task<AuthCallbackResult?> WaitForCallbackAsync(HttpListener listener, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (!cts.IsCancellationRequested)
        {
            var getContextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed != getContextTask)
            {
                break;
            }

            var context = await getContextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];
            var errorDescription = query["error_description"];

            var html = string.IsNullOrWhiteSpace(error)
                ? "<html><body><h2>Login complete</h2><p>You can close this tab and return to VardyParty.</p></body></html>"
                : "<html><body><h2>Login failed</h2><p>You can close this tab and return to VardyParty.</p></body></html>";

            var responseBuffer = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = string.IsNullOrWhiteSpace(error) ? 200 : 400;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseBuffer.Length;
            await context.Response.OutputStream.WriteAsync(responseBuffer);
            context.Response.OutputStream.Close();

            return new AuthCallbackResult
            {
                Code = code,
                State = state,
                Error = error,
                ErrorDescription = errorDescription
            };
        }

        return null;
    }

    private async Task<TokenExchangeResult> ExchangeAuthorizationCodeAsync(string code, string redirectUri,
        string codeVerifier, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(Auth0HttpClients.Name);
            var endpoint = BuildAuth0Url(_auth0Settings.Domain, "/oauth/token");

            using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _auth0Settings.ClientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
                ["audience"] = _auth0Settings.Audience
            }!), cancellationToken);

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (response.IsSuccessStatusCode && payload != null && !string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                return new TokenExchangeResult
                {
                    IsSuccess = true,
                    AccessToken = payload.AccessToken,
                    RefreshToken = payload.RefreshToken,
                    ExpiresIn = payload.ExpiresIn > 0 ? payload.ExpiresIn : 3600
                };
            }

            return new TokenExchangeResult
            {
                IsSuccess = false,
                Error = payload?.ErrorDescription ?? payload?.Error ?? "Unknown token exchange failure"
            };
        }
        catch (Exception ex)
        {
            return new TokenExchangeResult
            {
                IsSuccess = false,
                Error = ex.Message
            };
        }
    }

    private bool HasRequiredRole(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(_auth0Settings.RequiredRoleClaimType) ||
            string.IsNullOrWhiteSpace(_auth0Settings.RequiredRole))
        {
            return true;
        }

        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            var payloadBytes = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);

            if (!doc.RootElement.TryGetProperty(_auth0Settings.RequiredRoleClaimType, out var claim))
            {
                return false;
            }

            if (claim.ValueKind == JsonValueKind.String)
            {
                var raw = claim.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(v => string.Equals(v, _auth0Settings.RequiredRole, StringComparison.OrdinalIgnoreCase));
            }

            if (claim.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in claim.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String &&
                        string.Equals(item.GetString(), _auth0Settings.RequiredRole, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Auth0/Linux] Failed to validate required role from access token");
        }

        return false;
    }

    private sealed class StoredTokenPayload
    {
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public long ExpiresAtUnixSeconds { get; init; }
        public long LastRefreshedAtUnixSeconds { get; init; }
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }
        [JsonPropertyName("user_code")] public string? UserCode { get; init; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("interval")] public int Interval { get; init; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    }

    private sealed class AuthCallbackResult
    {
        public string? Code { get; init; }
        public string? State { get; init; }
        public string? Error { get; init; }
        public string? ErrorDescription { get; init; }
    }

    private sealed class TokenExchangeResult
    {
        public bool IsSuccess { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public int ExpiresIn { get; init; }
        public string? Error { get; init; }
    }
}
