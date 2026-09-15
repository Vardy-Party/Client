using System.Security.Cryptography;
using System.Text;

namespace VardyParty.Auth;

/// <summary>
/// PKCE authorize URL, S256 challenge, and loopback redirect rules shared by the Linux host.
/// OS storage and the browser/HttpListener stay in the host.
/// </summary>
public static class Auth0Pkce
{
    public const int StateByteLength = 32;
    public const int VerifierByteLength = 64;

    /// <summary>
    /// Default loopback callback for Linux browser PKCE. Must be listed in the
    /// Auth0 application Allowed Callback URLs alongside any mobile custom-scheme URI.
    /// </summary>
    public const string DefaultLinuxLoopbackRedirectUri = "http://127.0.0.1:4280/callback";

    /// <summary>
    /// Prefer an explicit loopback override, then a loopback <paramref name="redirectUri"/>,
    /// otherwise the Linux default. Custom schemes (e.g. vardyparty://) stay for MAUI
    /// WebAuthenticator and are not used for HttpListener browser login.
    /// </summary>
    public static Uri ResolveLinuxBrowserRedirectUri(string? redirectUri, string? loopbackRedirectUri = null)
    {
        if (TryGetLoopbackRedirectUri(loopbackRedirectUri, out var fromOverride))
            return fromOverride;
        if (TryGetLoopbackRedirectUri(redirectUri, out var fromSettings))
            return fromSettings;
        return new Uri(DefaultLinuxLoopbackRedirectUri);
    }

    public static Auth0PkceStart Start(Auth0Settings settings, Uri redirectUri)
    {
        var state = CreateRandomBase64Url(StateByteLength);
        var codeVerifier = CreateRandomBase64Url(VerifierByteLength);
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        return new Auth0PkceStart(
            state,
            codeVerifier,
            codeChallenge,
            BuildAuthorizeUrl(settings, redirectUri, state, codeChallenge));
    }

    public static string CreateRandomBase64Url(int byteLength)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength));

    public static string CreateCodeChallenge(string codeVerifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    public static string Base64UrlEncode(byte[] input)
        => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string BuildAuthorizeUrl(
        Auth0Settings settings,
        Uri redirectUri,
        string state,
        string codeChallenge)
    {
        var authEndpoint = Auth0OAuthClient.BuildUrl(settings.Domain, "/authorize");
        var scope = AuthTokenLifetime.EnsureOfflineAccess(settings.Scope);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = scope,
            ["audience"] = settings.Audience,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var queryString = string.Join("&", query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return $"{authEndpoint}?{queryString}";
    }

    public static bool TryGetLoopbackRedirectUri(string? redirectUri, out Uri uri)
    {
        if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            (parsed.IsLoopback || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    public static string BuildListenerPrefix(Uri redirectUri)
    {
        var path = string.IsNullOrWhiteSpace(redirectUri.AbsolutePath) || redirectUri.AbsolutePath == "/"
            ? "/"
            : redirectUri.AbsolutePath.TrimEnd('/') + "/";
        var port = redirectUri.Port;
        return $"{redirectUri.Scheme}://{redirectUri.Host}:{port}{path}";
    }

    public static string? DescribeCallbackFailure(
        string expectedState,
        string? actualState,
        string? code,
        string? error,
        string? errorDescription)
    {
        if (!string.IsNullOrWhiteSpace(error))
            return errorDescription ?? error;

        if (!string.Equals(actualState, expectedState, StringComparison.Ordinal))
            return "Auth0 callback state mismatch.";

        if (string.IsNullOrWhiteSpace(code))
            return "Auth0 callback did not include authorization code.";

        return null;
    }
}

public sealed record Auth0PkceStart(
    string State,
    string CodeVerifier,
    string CodeChallenge,
    string AuthorizeUrl);
