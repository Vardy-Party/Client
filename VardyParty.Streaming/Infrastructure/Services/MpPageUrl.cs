namespace VardyParty.Streaming;

/// <summary>
/// Match-page URLs that must use LocalService <c>POST /mp</c> (system Chrome)
/// instead of Facebook-style <c>GET /play</c>.
/// Keep in sync with <c>VardyParty.LocalService.Client.MpPageUrl</c>.
/// </summary>
public static class MpPageUrl
{
    public static bool IsMpPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host;
        if (ContainsIgnoreCase(host, "fctv"))
            return true;
        if (ContainsIgnoreCase(host, "mpoutqn4vebroad"))
            return true;
        if (ContainsIgnoreCase(host, "mpgreatest"))
            return true;
        if (host.StartsWith("jack", StringComparison.OrdinalIgnoreCase)
            && host.EndsWith(".my", StringComparison.OrdinalIgnoreCase))
            return true;

        return ContainsIgnoreCase(uri.AbsolutePath, "player.html");
    }

    public static bool UseMpEndpoint(string? streamUrl, IEnumerable<string>? capabilities) =>
        capabilities?.Any(c => string.Equals(c, "mp.chrome", StringComparison.OrdinalIgnoreCase)) == true
        && IsMpPage(streamUrl);

    private static bool ContainsIgnoreCase(string value, string fragment) =>
        value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
}
