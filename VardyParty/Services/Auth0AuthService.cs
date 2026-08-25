using Auth0.OidcClient;
using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Auth;
using VardyParty.Hosting;

namespace VardyParty;

public class Auth0AuthService : Auth0TokenSession
{
    private readonly IHttpMessageHandlerFactory _handlerFactory;

    public Auth0AuthService(
        ILogger<Auth0AuthService> logger,
        IOptions<Auth0Settings> auth0Settings,
        IAuth0OAuthClient oauth,
        IHttpMessageHandlerFactory handlerFactory)
        : base(logger, auth0Settings, oauth)
    {
        _handlerFactory = handlerFactory;
    }

    public override async Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTokenReadyAsync(cancellationToken, forceRefresh: false);
        if (HasValidToken) return new AuthLoginResult(true, AccessToken, null);

        if (!MauiProgram.IsWindowsPackaged)
        {
            Logger.LogWarning("[Auth0] Interactive login requires a packaged Windows app.");
            return new AuthLoginResult(false, null,
                "Interactive login requires a packaged Windows app. Use device sign-in instead.");
        }

        try
        {
            var client = BuildAuth0Client(Settings);
            Logger.LogInformation("[Auth0] Starting login...");

            LoginResult loginResult;
            try
            {
                Logger.LogInformation("[Auth0] Calling LoginAsync with audience: {Audience}", Settings.Audience);
                loginResult = await client.LoginAsync(new { audience = Settings.Audience }, cancellationToken);
                Logger.LogInformation("[Auth0] LoginAsync returned. IsError: {IsError}, Error: {Error}",
                    loginResult.IsError, loginResult.Error);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Logger.LogError(ex,
                    "[Auth0] Login timed out. This usually indicates network connectivity issues or DNS resolution problems.");
                return new AuthLoginResult(false, null, "Connection timed out. Please check your network connection.");
            }
            catch (Exception ex) when (ex.Message.Contains("SocketException") || ex.Message.Contains("Socket closed"))
            {
                Logger.LogError(ex, "[Auth0] Network error during login. Socket closed or connection failed.");
                return new AuthLoginResult(false, null, "Network error. Please check your internet connection.");
            }

            if (loginResult.IsError)
            {
                Logger.LogWarning("[Auth0] Login error: {Error}", loginResult.Error);
                return new AuthLoginResult(false, null, loginResult.Error);
            }

            Logger.LogInformation("[Auth0] Login successful. User: {User}, AccessToken present: {HasToken}",
                loginResult.User?.Identity?.Name ?? "unknown",
                !string.IsNullOrWhiteSpace(loginResult.AccessToken));

            if (!AcceptAccessToken(loginResult.AccessToken))
            {
                return new AuthLoginResult(false, null, null);
            }

            var expiration = loginResult.AccessTokenExpiration;
            var expiresIn = expiration == default
                ? 3600
                : (int)Math.Max(0, (expiration - DateTimeOffset.UtcNow).TotalSeconds);

            Logger.LogInformation("[Auth0] Setting token with {ExpiresIn}s expiry", expiresIn);
            await ApplyTokensAsync(loginResult.AccessToken, expiresIn, loginResult.RefreshToken);
            Logger.LogInformation("[Auth0] Token set successfully");
            return new AuthLoginResult(true, AccessToken, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Auth0] Interactive login failed");
            return new AuthLoginResult(false, null, ex.Message);
        }
    }

    protected override async Task LoadPersistedTokensAsync()
    {
        try
        {
            AccessToken = await SecureStorage.Default.GetAsync(AccessTokenKey);
            RefreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
            var expiresRaw = await SecureStorage.Default.GetAsync(ExpiresAtKey);
            if (long.TryParse(expiresRaw, out var unixSeconds))
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

            var lastRefreshedRaw = await SecureStorage.Default.GetAsync(LastRefreshedAtKey);
            if (long.TryParse(lastRefreshedRaw, out var lastRefreshedSeconds))
                LastRefreshedAt = DateTimeOffset.FromUnixTimeSeconds(lastRefreshedSeconds);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0] Failed to load tokens from secure storage");
        }
    }

    protected override async Task PersistTokensAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AccessToken))
                await SecureStorage.Default.SetAsync(AccessTokenKey, AccessToken);
            await SecureStorage.Default.SetAsync(ExpiresAtKey, ExpiresAt.ToUnixTimeSeconds().ToString());
            await SecureStorage.Default.SetAsync(LastRefreshedAtKey, LastRefreshedAt.ToUnixTimeSeconds().ToString());
            if (!string.IsNullOrWhiteSpace(RefreshToken))
                await SecureStorage.Default.SetAsync(RefreshTokenKey, RefreshToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0] Failed to persist tokens to secure storage");
        }
    }

    protected override Task ClearPersistedTokensAsync()
    {
        try
        {
            SecureStorage.Default.Remove(AccessTokenKey);
            SecureStorage.Default.Remove(RefreshTokenKey);
            SecureStorage.Default.Remove(ExpiresAtKey);
            SecureStorage.Default.Remove(LastRefreshedAtKey);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0] Failed to clear tokens from secure storage");
        }

        return Task.CompletedTask;
    }

    private Auth0Client BuildAuth0Client(Auth0Settings settings)
    {
        var domain = settings.Domain;
        var expectedDiscoveryUrl = $"https://{domain}/.well-known/openid-configuration";
        Logger.LogInformation("[Auth0] Building client for domain: {Domain}", domain);
        Logger.LogInformation("[Auth0] Expected discovery URL: {Url}", expectedDiscoveryUrl);

        var options = new Auth0ClientOptions
        {
            Domain = domain,
            ClientId = settings.ClientId,
            RedirectUri = settings.RedirectUri,
            PostLogoutRedirectUri = settings.PostLogoutRedirectUri,
            Scope = AuthTokenLifetime.EnsureOfflineAccess(settings.Scope),
            BackchannelHandler = new NonDisposingDelegatingHandler(
                _handlerFactory.CreateHandler(Auth0HttpClients.Name))
        };

        return new Auth0Client(options);
    }

    private const string AccessTokenKey = "Auth0.AccessToken";
    private const string RefreshTokenKey = "Auth0.RefreshToken";
    private const string ExpiresAtKey = "Auth0.ExpiresAt";
    private const string LastRefreshedAtKey = "Auth0.LastRefreshedAt";
}
