namespace VardyParty.Auth;

public class Auth0Settings
{
    public static string SectionName => "Auth0";
    public required string Domain { get; set; }
    public required string ClientId { get; set; }
    public required string Audience { get; set; }
    public required string Scope { get; set; }
    public required string CallbackScheme { get; set; }
    public required string RedirectUri { get; set; }
    public required string PostLogoutRedirectUri { get; set; }

    /// <summary>
    /// Optional loopback callback for browser PKCE when <see cref="RedirectUri"/>
    /// is a custom scheme (MAUI). Used by Linux and unpackaged Windows. Defaults to
    /// <c>http://127.0.0.1:4280/callback</c> when empty.
    /// </summary>
    public string? LoopbackRedirectUri { get; set; }
    public required int TokenLeewaySeconds { get; set; }
    public int SlidingRefreshAfterSeconds { get; set; } = 900;
    public required string RequiredRoleClaimType { get; set; }
    public required string RequiredRole { get; set; }
}