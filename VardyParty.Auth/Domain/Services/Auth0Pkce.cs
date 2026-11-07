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

    /// <summary>
    /// HTML for the loopback browser callback. Visual language matches the API
    /// installers homepage (<c>installers.css</c>: #003090 header, #eef2f8 page, white card).
    /// Tries to close the tab when the browser allows it.
    /// </summary>
    public static string BuildBrowserCallbackHtml(bool success)
    {
        var title = success ? "Login complete" : "Login failed";
        var body = success
            ? "Returning to VardyParty…"
            : "Sign-in failed. You can close this tab and return to VardyParty.";
        // Same crest as Vardy-Party/API installersPage LOGO_SVG (simplified for inline CSS pages).
        const string logoSvg =
            "<svg class=\"logo\" viewBox=\"0 0 456 456\" aria-hidden=\"true\">" +
            "<rect width=\"456\" height=\"456\" rx=\"80\" fill=\"#003090\"/>" +
            "<g transform=\"translate(228,228)\">" +
            "<circle cx=\"0\" cy=\"0\" r=\"150\" fill=\"#0052B4\" stroke=\"#ffffff\" stroke-width=\"8\"/>" +
            "<path d=\"M 0,-128 L 42,-53 L 0,-12 L -42,-53 Z\" fill=\"#003090\"/>" +
            "<path d=\"M 42,-53 L 106,-68 L 118,0 L 53,27 L 0,-12 Z\" fill=\"#E8E8E8\"/>" +
            "<path d=\"M -42,-53 L -106,-68 L -118,0 L -53,27 L 0,-12 Z\" fill=\"#E8E8E8\"/>" +
            "<path d=\"M 53,27 L 78,93 L 27,128 L -27,128 L -53,27 Z\" fill=\"#E8E8E8\"/>" +
            "<path d=\"M 0,-12 L 53,27 L -53,27 Z\" fill=\"#D5D5D5\"/>" +
            "</g></svg>";

        return string.Concat(
            "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />",
            "<title>", title, " — VardyParty</title>",
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />",
            "<style>",
            "*{box-sizing:border-box}",
            "body{margin:0;min-height:100vh;font-family:\"Segoe UI\",Arial,sans-serif;",
            "background:#eef2f8;color:#142033;display:flex;flex-direction:column}",
            "header{background:#003090;color:#fff;padding:28px 24px 26px;text-align:center}",
            ".brand{display:inline-flex;flex-direction:column;align-items:center;gap:14px;color:inherit}",
            ".logo{width:72px;height:72px}",
            "header h1{margin:0;font-size:2.4rem;font-weight:700;letter-spacing:-0.03em;line-height:1.1}",
            "header p{margin:10px auto 0;max-width:28rem;color:#d6e4ff;font-size:1.25rem;line-height:1.4}",
            "main{max-width:40rem;margin:0 auto;padding:28px 24px 48px;width:100%}",
            ".card{padding:22px 24px 20px;border-radius:16px;background:#fff;",
            "box-shadow:0 10px 30px rgba(0,32,96,0.08);text-align:center}",
            ".card h2{margin:0 0 12px;font-size:1.6rem;font-weight:700;line-height:1.2;color:#142033}",
            ".card p{margin:0;font-size:1.2rem;line-height:1.45;color:#243044}",
            "#fallback{display:none;margin-top:16px;color:#243044}",
            "</style></head><body>",
            "<header><div class=\"brand\">", logoSvg,
            "<h1>VardyParty</h1>",
            "<p>", title, "</p></div></header>",
            "<main><article class=\"card\">",
            "<h2>", title, "</h2>",
            "<p id=\"status\">", body, "</p>",
            "<p id=\"fallback\">If this tab is still open, you can close it — VardyParty should be in front.</p>",
            "</article></main>",
            "<script>(function(){",
            "try{window.open('','_self');}catch(e){}",
            "try{window.close();}catch(e){}",
            "setTimeout(function(){",
            "try{window.close();}catch(e){}",
            "var f=document.getElementById('fallback');if(f)f.style.display='block';",
            "var s=document.getElementById('status');if(s)s.textContent='You can close this tab.';",
            "},400);})();</script></body></html>");
    }
}

public sealed record Auth0PkceStart(
    string State,
    string CodeVerifier,
    string CodeChallenge,
    string AuthorizeUrl);
