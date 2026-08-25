using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VardyParty.Configuration;

namespace VardyParty.Auth;

public interface IAuth0OAuthClient
{
    Task<Auth0DeviceCodeHttpResult> RequestDeviceCodeAsync(Auth0Settings settings, CancellationToken cancellationToken);
    Task<Auth0TokenHttpResult> ExchangeDeviceCodeAsync(Auth0Settings settings, string deviceCode, CancellationToken cancellationToken);
    Task<Auth0TokenHttpResult> ExchangeAuthorizationCodeAsync(
        Auth0Settings settings,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken);
    Task<Auth0TokenHttpResult> RefreshAsync(Auth0Settings settings, string refreshToken, CancellationToken cancellationToken);
}

public sealed record Auth0DeviceCodeHttpResult(bool IsSuccess, AuthDeviceCode? DeviceCode, string? Error, string Body);

public sealed record Auth0TokenHttpResult(
    bool IsSuccess,
    string? AccessToken,
    int ExpiresIn,
    string? RefreshToken,
    string? Error,
    string? ErrorDescription);

public sealed class Auth0OAuthClient(
    IHttpClientFactory httpClientFactory,
    ILogger<Auth0OAuthClient> logger) : IAuth0OAuthClient
{
    public static string BuildUrl(string domain, string path)
    {
        var normalized = domain.Trim();
        normalized = normalized.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.TrimEnd('/');
        return $"https://{normalized}{path}";
    }

    public async Task<Auth0DeviceCodeHttpResult> RequestDeviceCodeAsync(
        Auth0Settings settings,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(Auth0HttpClients.Name);
        var endpoint = BuildUrl(settings.Domain, "/oauth/device/code");
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", settings.ClientId)
        };

        if (!string.IsNullOrWhiteSpace(settings.Audience))
            form.Add(new KeyValuePair<string, string>("audience", settings.Audience));

        form.Add(new KeyValuePair<string, string>("scope", AuthTokenLifetime.EnsureOfflineAccess(settings.Scope)));

        logger.LogInformation("[Auth0] Requesting device code from {Endpoint}", endpoint);
        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var authError = TryReadOAuthError(body);
            var message = !string.IsNullOrWhiteSpace(authError?.ErrorDescription)
                ? authError.ErrorDescription
                : !string.IsNullOrWhiteSpace(authError?.Error)
                    ? authError.Error
                    : $"Sign-in failed ({(int)response.StatusCode}). Check Auth0 device-code grant.";
            logger.LogWarning(
                "[Auth0] Device code request failed: {Status} {Error} {Description}",
                (int)response.StatusCode,
                authError?.Error,
                authError?.ErrorDescription);
            return new Auth0DeviceCodeHttpResult(false, null, message, body);
        }

        var payload = System.Text.Json.JsonSerializer.Deserialize<DeviceCodeResponse>(body);
        if (payload == null || string.IsNullOrWhiteSpace(payload.DeviceCode) ||
            string.IsNullOrWhiteSpace(payload.UserCode) ||
            string.IsNullOrWhiteSpace(payload.VerificationUri))
        {
            logger.LogWarning("[Auth0] Device code response missing required fields");
            return new Auth0DeviceCodeHttpResult(false, null, "Auth0 device sign-in returned an incomplete response.", body);
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
        return new Auth0DeviceCodeHttpResult(true, deviceCode, null, body);
    }

    public async Task<Auth0TokenHttpResult> ExchangeDeviceCodeAsync(
        Auth0Settings settings,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(Auth0HttpClients.Name);
        var endpoint = BuildUrl(settings.Domain, "/oauth/token");
        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(
            new Dictionary<string, string?>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = settings.ClientId,
                ["audience"] = settings.Audience
            }!), cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return ToTokenResult(response.IsSuccessStatusCode, payload);
    }

    public async Task<Auth0TokenHttpResult> ExchangeAuthorizationCodeAsync(
        Auth0Settings settings,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(Auth0HttpClients.Name);
        var endpoint = BuildUrl(settings.Domain, "/oauth/token");
        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = settings.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["audience"] = settings.Audience
        }!), cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return ToTokenResult(response.IsSuccessStatusCode, payload);
    }

    public async Task<Auth0TokenHttpResult> RefreshAsync(
        Auth0Settings settings,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(Auth0HttpClients.Name);
        var endpoint = BuildUrl(settings.Domain, "/oauth/token");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = settings.ClientId,
            ["audience"] = settings.Audience
        }!), cts.Token);

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cts.Token);
        return ToTokenResult(response.IsSuccessStatusCode, payload);
    }

    private static Auth0TokenHttpResult ToTokenResult(bool success, TokenResponse? payload)
        => new(
            success && payload != null && !string.IsNullOrWhiteSpace(payload.AccessToken),
            payload?.AccessToken,
            payload?.ExpiresIn ?? 0,
            payload?.RefreshToken,
            payload?.Error,
            payload?.ErrorDescription);

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

        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

        [JsonPropertyName("error")] public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}
