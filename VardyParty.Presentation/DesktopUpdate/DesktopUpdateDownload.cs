namespace VardyParty.Presentation;

public static class DesktopUpdateDownload
{
    public const long MaxAssetBytes = 512L * 1024 * 1024;

    public static string FileNameFromAsset(string? assetName)
    {
        var name = Path.GetFileName(assetName ?? "");
        if (string.IsNullOrWhiteSpace(name)
            || name != assetName
            || name.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update asset name is not a plain file name.");
        }

        return name;
    }

    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
}
