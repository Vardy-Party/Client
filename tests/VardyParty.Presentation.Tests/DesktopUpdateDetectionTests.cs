using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// What counts as a desktop update: newer tag than the running app, matching
/// installer for this OS/arch, not draft/prerelease, with a download URL.
/// Age is covered in <see cref="DesktopUpdateMaturityTests"/>.
/// </summary>
public class DesktopUpdateDetectionTests
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly AppReleaseVersion _running = new(2, 0, 0, 159);

    private DateTimeOffset ThreeDaysAgo => _now - TimeSpan.FromDays(3);

    [Theory]
    [InlineData("2.1.0-b160", 2, 1, 0, 160)]
    [InlineData("v2.1.0-b160", 2, 1, 0, 160)]
    [InlineData("V2.1.0-b160", 2, 1, 0, 160)]
    [InlineData("2.0.0-b159", 2, 0, 0, 159)]
    public void TryParseTag_GitHubReleaseTags(string tag, int major, int minor, int patch, int build)
    {
        // Arrange / Act
        var parsed = AppReleaseVersion.TryParseTag(tag, out var version);

        // Assert
        Assert.True(parsed);
        Assert.Equal(new AppReleaseVersion(major, minor, patch, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("2.1")]
    [InlineData("2.1.0-b")]
    [InlineData("2.1.0-b-1")]
    public void TryParseTag_Invalid_ReturnsFalse(string? tag)
    {
        // Arrange / Act
        var parsed = AppReleaseVersion.TryParseTag(tag, out var version);

        // Assert
        Assert.False(parsed);
        Assert.Equal(default, version);
    }

    [Fact]
    public void IsNewerThan_DisplayThenBuild()
    {
        // Arrange
        var running = new AppReleaseVersion(2, 0, 0, 159);

        // Assert
        Assert.True(new AppReleaseVersion(2, 0, 0, 160).IsNewerThan(running));
        Assert.True(new AppReleaseVersion(2, 0, 1, 0).IsNewerThan(running));
        Assert.True(new AppReleaseVersion(2, 1, 0, 1).IsNewerThan(running));
        Assert.False(running.IsNewerThan(running));
        Assert.False(new AppReleaseVersion(1, 9, 9, 999).IsNewerThan(running));
        Assert.False(new AppReleaseVersion(2, 0, 0, 158).IsNewerThan(running));
    }

    [Theory]
    [InlineData(true, "VardyParty-windows-v2.1.0-b160.msix", DesktopUpdatePlatform.Windows)]
    [InlineData(true, "vardyparty-windows-v2.1.0-b160.MSIX", DesktopUpdatePlatform.Windows)]
    [InlineData(true, "VardyParty-linux-x64-v2.1.0+160.snap", DesktopUpdatePlatform.LinuxX64)]
    [InlineData(true, "VardyParty-linux-arm64-v2.1.0+160.snap", DesktopUpdatePlatform.LinuxArm64)]
    [InlineData(false, "VardyParty-linux-x64-v2.1.0+160.snap", DesktopUpdatePlatform.Windows)]
    [InlineData(false, "VardyParty-linux-arm64-v2.1.0+160.snap", DesktopUpdatePlatform.LinuxX64)]
    [InlineData(false, "VardyParty-linux-x64-v2.1.0+160.snap", DesktopUpdatePlatform.LinuxArm64)]
    [InlineData(false, "VardyParty-windows-v2.1.0-b160.msix", DesktopUpdatePlatform.LinuxX64)]
    [InlineData(false, "VardyParty-windows-v2.1.0-b160.msix", DesktopUpdatePlatform.LinuxArm64)]
    [InlineData(false, "com.vardyparty-Signed.apk", DesktopUpdatePlatform.Windows)]
    [InlineData(false, "VardyParty-windows-v2.1.0-b160.exe", DesktopUpdatePlatform.Windows)]
    [InlineData(false, "", DesktopUpdatePlatform.Windows)]
    [InlineData(false, null, DesktopUpdatePlatform.Windows)]
    public void AssetMatches_OnlyThisOsInstaller(bool expected, string? name, DesktopUpdatePlatform platform)
    {
        // Arrange / Act
        var match = DesktopUpdatePolicy.AssetMatches(name, platform);

        // Assert
        Assert.Equal(expected, match);
    }

    [Fact]
    public void DetectPlatform_MatchesThisOs()
    {
        // Arrange / Act
        var platform = DesktopUpdatePolicy.DetectPlatform();

        // Assert
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(DesktopUpdatePlatform.Windows, platform);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.True(
                platform is DesktopUpdatePlatform.LinuxX64 or DesktopUpdatePlatform.LinuxArm64);
        }
        else
        {
            Assert.Null(platform);
        }
    }

    [Fact]
    public void SelectOffer_SameTagAsRunning_IsNotAnUpdate()
    {
        // Arrange
        var running = new AppReleaseVersion(2, 1, 0, 160);
        var releases = new[] { Stable("2.1.0-b160", WindowsMsix("2.1.0-b160")) };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_OlderDisplayThanRunning_IsNotAnUpdate()
    {
        // Arrange
        var releases = new[] { Stable("1.9.0-b200", WindowsMsix("1.9.0-b200")) };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_SameDisplayHigherBuild_IsAnUpdate()
    {
        // Arrange
        var releases = new[] { Stable("2.0.0-b160", WindowsMsix("2.0.0-b160")) };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("2.0.0-b160", offer.Tag);
        Assert.Equal(new AppReleaseVersion(2, 0, 0, 160), offer.Version);
    }

    [Fact]
    public void SelectOffer_SameDisplayOlderBuild_IsNotAnUpdate()
    {
        // Arrange
        var releases = new[] { Stable("2.0.0-b158", WindowsMsix("2.0.0-b158")) };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_OnlyPromotesLaterThanRunning_WhenFeedHasOlderAndNewer()
    {
        // Arrange: running 2.0.0-b159. Older tags stay in the feed but must
        // never be offered; only the newest tag later than running wins.
        var releases = new[]
        {
            Stable("1.7.113-b146", WindowsMsix("1.7.113-b146")),
            Stable("2.0.0-b158", WindowsMsix("2.0.0-b158")),
            Stable("2.0.0-b159", WindowsMsix("2.0.0-b159")),
            Stable("2.0.1-b161", WindowsMsix("2.0.1-b161")),
            Stable("2.1.0-b160", WindowsMsix("2.1.0-b160")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("2.1.0-b160", offer.Tag);
        Assert.True(offer.Version.IsNewerThan(_running));
        Assert.True(offer.Version.CompareTo(new AppReleaseVersion(2, 0, 1, 161)) > 0);
    }

    [Fact]
    public void SelectOffer_SkipsDraftPrereleaseUnparseableAndUnpublished()
    {
        // Arrange
        var releases = new[]
        {
            new GitHubReleaseSnapshot("2.2.0-b170", Draft: true, Prerelease: false, ThreeDaysAgo, [WindowsMsix("2.2.0-b170")]),
            new GitHubReleaseSnapshot("2.2.0-b171", Draft: false, Prerelease: true, ThreeDaysAgo, [WindowsMsix("2.2.0-b171")]),
            new GitHubReleaseSnapshot("2.2.0-b172", Draft: false, Prerelease: false, PublishedAt: null, [WindowsMsix("2.2.0-b172")]),
            new GitHubReleaseSnapshot("not-a-tag", Draft: false, Prerelease: false, ThreeDaysAgo, [WindowsMsix("2.2.0-b173")]),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_SkipsWrongAssetAndEmptyDownloadUrl()
    {
        // Arrange
        var releases = new[]
        {
            Stable("2.2.0-b172", new GitHubReleaseAssetSnapshot("notes.txt", "https://example/notes")),
            Stable("2.2.0-b173", new GitHubReleaseAssetSnapshot(
                "VardyParty-windows-v2.2.0-b173.msix",
                "")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_PicksNewestMatureMatchingAsset()
    {
        // Arrange
        var releases = new[]
        {
            Stable("2.0.1-b161", LinuxSnap("x64", "2.0.1", "161"), LinuxSnapSig("x64", "2.0.1", "161")),
            Stable("2.1.0-b160", LinuxSnap("x64", "2.1.0", "160"), LinuxSnapSig("x64", "2.1.0", "160")),
            Stable("2.3.0-b180", LinuxSnap("arm64", "2.3.0", "180"), LinuxSnapSig("arm64", "2.3.0", "180")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.LinuxX64, _now);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("2.1.0-b160", offer.Tag);
        Assert.Contains("linux-x64", offer.AssetName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://example/x64.snap.minisig", offer.SignatureUrl);
    }

    [Fact]
    public void SelectOffer_WindowsIgnoresLinuxSnap()
    {
        // Arrange
        var releases = new[] { Stable("2.1.0-b160", LinuxSnap("x64", "2.1.0", "160")) };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_UsesFirstMatchingAssetOnTheRelease()
    {
        // Arrange
        var releases = new[]
        {
            Stable(
                "2.1.0-b160",
                new GitHubReleaseAssetSnapshot("README.md", "https://example/readme"),
                WindowsMsix("2.1.0-b160"),
                LinuxSnap("x64", "2.1.0", "160")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.Windows, _now);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("VardyParty-windows-v2.1.0-b160.msix", offer.AssetName);
        Assert.Equal("https://example/2.1.0-b160.msix", offer.DownloadUrl);
    }

    [Fact]
    public void SelectOffer_LinuxSnapWithoutMinisig_IsNotOffered()
    {
        // Arrange
        var releases = new[] { Stable("2.1.0-b160", LinuxSnap("x64", "2.1.0", "160")) };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, _running, DesktopUpdatePlatform.LinuxX64, _now);

        // Assert
        Assert.Null(offer);
    }

    private GitHubReleaseSnapshot Stable(
        string tag,
        params GitHubReleaseAssetSnapshot[] assets) =>
        new(tag, Draft: false, Prerelease: false, ThreeDaysAgo, assets);

    private static GitHubReleaseAssetSnapshot WindowsMsix(string tag) =>
        new($"VardyParty-windows-v{tag}.msix", $"https://example/{tag}.msix");

    private static GitHubReleaseAssetSnapshot LinuxSnap(string arch, string display, string build) =>
        new($"VardyParty-linux-{arch}-v{display}+{build}.snap", $"https://example/{arch}.snap");

    private static GitHubReleaseAssetSnapshot LinuxSnapSig(string arch, string display, string build) =>
        new(
            $"VardyParty-linux-{arch}-v{display}+{build}.snap.minisig",
            $"https://example/{arch}.snap.minisig");
}
