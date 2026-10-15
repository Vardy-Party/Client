using System.IO.Compression;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VardyParty.Presentation;

namespace VardyParty.Platforms.Windows;

internal static class MsixPackageSignature
{
    private const string SignatureEntry = "AppxSignature.p7x";

    public static void EnsureDownloadedMatchesInstalled(string downloadedMsixPath)
    {
        var installedRoot = global::Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
        var installedSignature = Path.Combine(installedRoot, SignatureEntry);
        if (!File.Exists(installedSignature))
        {
            throw new InvalidOperationException(
                "Installed package has no AppxSignature.p7x; refusing to apply an MSIX update.");
        }

        using var installed = ReadSigner(File.ReadAllBytes(installedSignature));
        using var downloaded = ReadSigner(ReadSignatureFromMsix(downloadedMsixPath));
        MsixSignerPin.EnsureSameSigner(
            global::Windows.ApplicationModel.Package.Current.Id.Publisher,
            MsixSignerPin.ThumbprintOf(installed),
            downloaded.Subject,
            MsixSignerPin.ThumbprintOf(downloaded));
    }

    internal static byte[] ReadSignatureFromMsix(string msixPath)
    {
        using var zip = ZipFile.OpenRead(msixPath);
        var entry = zip.GetEntry(SignatureEntry)
            ?? throw new InvalidOperationException("Downloaded MSIX has no AppxSignature.p7x.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    internal static X509Certificate2 ReadSigner(byte[] pkcs7)
    {
        var cms = new SignedCms();
        cms.Decode(AppxSignaturePayload.Unwrap(pkcs7));
        cms.CheckSignature(verifySignatureOnly: true);
        var cert = cms.SignerInfos.Count > 0
            ? cms.SignerInfos[0].Certificate
            : cms.Certificates.Count > 0 ? cms.Certificates[0] : null;
        return cert ?? throw new InvalidOperationException("MSIX PKCS#7 signature has no signer certificate.");
    }
}
