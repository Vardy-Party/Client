using System;
using System.Text.Json;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// GitHub release <c>2.1.2-b164</c> names the Linux snaps with a snap-revision
/// plus (<c>v2.1.2+164.snap</c>) while the tag keeps <c>-b164</c>, and GitHub
/// percent-encodes that plus in <c>browser_download_url</c>. The Linux updater
/// must still pair each snap with its sibling <c>.minisig</c>.
/// </summary>
public class GitHubReleaseFeedTests
{
    private static readonly DateTimeOffset Published =
        DateTimeOffset.Parse("2026-09-01T21:21:37Z");

    // Minimized GitHub Releases API document for tag 2.1.2-b164 (asset names
    // and download URLs copied from the live release; unused fields omitted).
    private const string Release212b164Json = """
        [
          {
            "tag_name": "2.1.2-b164",
            "draft": false,
            "prerelease": false,
            "published_at": "2026-09-01T21:21:37Z",
            "assets": [
              {
                "name": "minisign.pub",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/minisign.pub"
              },
              {
                "name": "VardyParty-android-v2.1.2-b164.apk",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-android-v2.1.2-b164.apk"
              },
              {
                "name": "VardyParty-linux-arm64-v2.1.2+164.snap",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-linux-arm64-v2.1.2%2B164.snap"
              },
              {
                "name": "VardyParty-linux-arm64-v2.1.2+164.snap.minisig",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-linux-arm64-v2.1.2%2B164.snap.minisig"
              },
              {
                "name": "VardyParty-linux-x64-v2.1.2+164.snap",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-linux-x64-v2.1.2%2B164.snap"
              },
              {
                "name": "VardyParty-linux-x64-v2.1.2+164.snap.minisig",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-linux-x64-v2.1.2%2B164.snap.minisig"
              },
              {
                "name": "VardyParty-macos-v2.1.2-b164.tar.gz",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-macos-v2.1.2-b164.tar.gz"
              },
              {
                "name": "VardyParty-windows-v2.1.2-b164.msix",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-windows-v2.1.2-b164.msix"
              },
              {
                "name": "vardyparty.cer",
                "browser_download_url": "https://github.com/example/Client/releases/download/2.1.2-b164/vardyparty.cer"
              }
            ]
          }
        ]
        """;

    [Fact]
    public void ReadArray_Release212b164_KeepsPlusInSnapNamesAndEncodedUrls()
    {
        // Arrange
        using var doc = JsonDocument.Parse(Release212b164Json);

        // Act
        var releases = GitHubReleaseFeed.ReadArray(doc.RootElement);

        // Assert
        var release = Assert.Single(releases);
        Assert.Equal("2.1.2-b164", release.TagName);
        Assert.False(release.Draft);
        Assert.False(release.Prerelease);
        Assert.Equal(Published, release.PublishedAt);
        Assert.Contains(
            release.Assets,
            a => a.Name == "VardyParty-linux-x64-v2.1.2+164.snap"
                && a.BrowserDownloadUrl.Contains("%2B164.snap", StringComparison.Ordinal)
                && !a.BrowserDownloadUrl.EndsWith(".minisig", StringComparison.Ordinal));
        Assert.Contains(
            release.Assets,
            a => a.Name == "VardyParty-linux-x64-v2.1.2+164.snap.minisig"
                && a.BrowserDownloadUrl.EndsWith("%2B164.snap.minisig", StringComparison.Ordinal));
        Assert.Contains(
            release.Assets,
            a => a.Name == "VardyParty-linux-arm64-v2.1.2+164.snap");
        Assert.Contains(
            release.Assets,
            a => a.Name == "VardyParty-linux-arm64-v2.1.2+164.snap.minisig");
    }

    [Theory]
    [InlineData(DesktopUpdatePlatform.LinuxX64, "VardyParty-linux-x64-v2.1.2+164.snap", "%2B164.snap.minisig")]
    [InlineData(DesktopUpdatePlatform.LinuxArm64, "VardyParty-linux-arm64-v2.1.2+164.snap", "%2B164.snap.minisig")]
    public void SelectOffer_Release212b164_LinuxPairsSnapWithMinisig(
        DesktopUpdatePlatform platform,
        string snapName,
        string signatureUrlSuffix)
    {
        // Arrange
        using var doc = JsonDocument.Parse(Release212b164Json);
        var releases = GitHubReleaseFeed.ReadArray(doc.RootElement);
        var running = new AppReleaseVersion(2, 1, 1, 163);
        var utcNow = Published + DesktopUpdatePolicy.Maturity;

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(releases, running, platform, utcNow);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("2.1.2-b164", offer.Tag);
        Assert.Equal(new AppReleaseVersion(2, 1, 2, 164), offer.Version);
        Assert.Equal(snapName, offer.AssetName);
        Assert.Contains("%2B164.snap", offer.DownloadUrl, StringComparison.Ordinal);
        Assert.False(offer.DownloadUrl.EndsWith(".minisig", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(offer.SignatureUrl);
        Assert.EndsWith(signatureUrlSuffix, offer.SignatureUrl, StringComparison.Ordinal);
        Assert.True(DesktopUpdateDownload.IsAllowedDownloadUrl(offer.DownloadUrl));
        Assert.True(DesktopUpdateDownload.IsAllowedDownloadUrl(offer.SignatureUrl));
        Assert.Equal(snapName, DesktopUpdateDownload.FileNameFromAsset(offer.AssetName));
    }

    [Fact]
    public void SelectOffer_Release212b164_WindowsIgnoresSnapsAndMinisigs()
    {
        // Arrange
        using var doc = JsonDocument.Parse(Release212b164Json);
        var releases = GitHubReleaseFeed.ReadArray(doc.RootElement);
        var running = new AppReleaseVersion(2, 1, 1, 163);
        var utcNow = Published + DesktopUpdatePolicy.Maturity;

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, running, DesktopUpdatePlatform.Windows, utcNow);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("VardyParty-windows-v2.1.2-b164.msix", offer.AssetName);
        Assert.Null(offer.SignatureUrl);
    }

    [Fact]
    public void LiveX64Minisig_ParsesAsHashedEdAndMatchesEmbeddedLinuxKey()
    {
        // Arrange: signature bytes from the 2.1.2-b164 x64 snap.minisig
        // (the snap itself is not needed to check key-id + ED format).
        const string signature = """
            untrusted comment: signature from Vardy Party minisign key
            RURs0KfOpM7k/CtiIaXLgAIjHf2wuyeOQiPkl7yZcaHTVCI8/MpBldvezps9UXibtEV6u2fRJH4vNJ3v7+0Nizh7UHe1ogviXw8=
            trusted comment: timestamp:1788297959
            O5PBsShjAtBVrFmnTunjPBNUXaHFgr4X7heWDH0glJjqWRR5/VZWuKXCGSFZgGpyh2mBH9orjTKiKGG8UxP3AQ==
            """;
        using var empty = new System.IO.MemoryStream();

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MinisignHashed.Verify(empty, signature, MinisignPublicKeys.Linux));

        // Assert
        Assert.Contains("not valid", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key id", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incomplete", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectOffer_Release212b164_YoungerThanTwoDays_IsNotOffered()
    {
        // Arrange
        using var doc = JsonDocument.Parse(Release212b164Json);
        var releases = GitHubReleaseFeed.ReadArray(doc.RootElement);
        var running = new AppReleaseVersion(2, 1, 1, 163);
        var utcNow = Published + TimeSpan.FromHours(12);

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, running, DesktopUpdatePlatform.LinuxX64, utcNow);

        // Assert
        Assert.Null(offer);
    }
}
