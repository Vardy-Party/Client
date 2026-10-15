using System.Security.Cryptography.X509Certificates;

namespace VardyParty.Presentation;

/// <summary>
/// Sideload MSIX updates must be signed with the same certificate as the
/// installed package (publisher + thumbprint).
/// </summary>
public static class MsixSignerPin
{
    public static void EnsureSameSigner(
        string installedPublisher,
        string installedThumbprint,
        string downloadedPublisher,
        string downloadedThumbprint)
    {
        if (string.IsNullOrWhiteSpace(installedThumbprint)
            || string.IsNullOrWhiteSpace(downloadedThumbprint))
        {
            throw new InvalidOperationException("MSIX signer certificate is missing.");
        }

        if (!string.Equals(
                installedThumbprint.Replace(" ", "", StringComparison.Ordinal),
                downloadedThumbprint.Replace(" ", "", StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Downloaded MSIX is not signed with this app's certificate.");
        }

        if (!SamePublisher(installedPublisher, downloadedPublisher))
        {
            throw new InvalidOperationException(
                "Downloaded MSIX publisher does not match the installed package.");
        }
    }

    public static bool SamePublisher(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(NormalizeDn(left), NormalizeDn(right), StringComparison.OrdinalIgnoreCase);
    }

    public static string ThumbprintOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Thumbprint.Replace(" ", "", StringComparison.Ordinal);
    }

    private static string NormalizeDn(string distinguishedName) =>
        distinguishedName.Replace(" ", "", StringComparison.Ordinal).Trim();
}
