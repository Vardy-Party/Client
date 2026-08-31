using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class DesktopUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly AppReleaseVersion Running = new(2, 0, 0, 159);

    [Fact]
    public void TryParseTag_DisplayAndBuild()
    {
        // Arrange / Act
        var parsed = AppReleaseVersion.TryParseTag("2.1.0-b160", out var version);

        // Assert
        Assert.True(parsed);
        Assert.Equal(new AppReleaseVersion(2, 1, 0, 160), version);
    }

    [Theory]
    [InlineData(true, "VardyParty-windows-v2.1.0-b160.msix", DesktopUpdatePlatform.Windows)]
    [InlineData(true, "VardyParty-linux-x64-v2.1.0+160.snap", DesktopUpdatePlatform.LinuxX64)]
    [InlineData(true, "VardyParty-linux-arm64-v2.1.0+160.snap", DesktopUpdatePlatform.LinuxArm64)]
    [InlineData(false, "VardyParty-linux-x64-v2.1.0+160.snap", DesktopUpdatePlatform.Windows)]
    [InlineData(false, "VardyParty-windows-v2.1.0-b160.msix", DesktopUpdatePlatform.LinuxX64)]
    public void AssetMatches_PlatformInstallers(bool expected, string name, DesktopUpdatePlatform platform)
    {
        // Arrange / Act
        var match = DesktopUpdatePolicy.AssetMatches(name, platform);

        // Assert
        Assert.Equal(expected, match);
    }

    [Fact]
    public void SelectOffer_IgnoresYoungReleases()
    {
        // Arrange
        var releases = new[]
        {
            Release("2.1.0-b160", Now.AddHours(-12), WindowsMsix("2.1.0-b160")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(releases, Running, DesktopUpdatePlatform.Windows, Now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_MatureWindowsMsix_WhenNewer()
    {
        // Arrange
        var published = Now - DesktopUpdatePolicy.Maturity - TimeSpan.FromMinutes(1);
        var releases = new[]
        {
            Release("2.1.0-b160", published, WindowsMsix("2.1.0-b160")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(releases, Running, DesktopUpdatePlatform.Windows, Now);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("2.1.0-b160", offer.Tag);
        Assert.Contains("windows", offer.AssetName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectOffer_SameVersion_IsNotAnUpdate()
    {
        // Arrange
        var published = Now - TimeSpan.FromDays(10);
        var running = new AppReleaseVersion(2, 1, 0, 160);
        var releases = new[]
        {
            Release("2.1.0-b160", published, WindowsMsix("2.1.0-b160")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(releases, running, DesktopUpdatePlatform.Windows, Now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_SkipsDraftPrereleaseAndMissingAsset()
    {
        // Arrange
        var published = Now - TimeSpan.FromDays(10);
        var releases = new[]
        {
            new GitHubReleaseSnapshot("2.2.0-b170", Draft: true, Prerelease: false, published, [WindowsMsix("2.2.0-b170")]),
            new GitHubReleaseSnapshot("2.2.0-b171", Draft: false, Prerelease: true, published, [WindowsMsix("2.2.0-b171")]),
            Release("2.2.0-b172", published, new GitHubReleaseAssetSnapshot("notes.txt", "https://example/notes")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(releases, Running, DesktopUpdatePlatform.Windows, Now);

        // Assert
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_PicksNewestMatureWithMatchingSnap()
    {
        // Arrange
        var published = Now - TimeSpan.FromDays(10);
        var releases = new[]
        {
            Release("2.0.1-b161", published, LinuxSnap("x64", "2.0.1", "161")),
            Release("2.1.0-b160", published, LinuxSnap("x64", "2.1.0", "160")),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, Running, DesktopUpdatePlatform.LinuxX64, Now);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("2.1.0-b160", offer.Tag);
    }

    [Fact]
    public void IsMature_RequiresTwoDays()
    {
        // Arrange / Act / Assert
        Assert.False(DesktopUpdatePolicy.IsMature(Now.AddDays(-1.9), Now));
        Assert.True(DesktopUpdatePolicy.IsMature(Now.AddDays(-2), Now));
    }

    private static GitHubReleaseSnapshot Release(
        string tag,
        DateTimeOffset published,
        GitHubReleaseAssetSnapshot asset) =>
        new(tag, Draft: false, Prerelease: false, published, [asset]);

    private static GitHubReleaseAssetSnapshot WindowsMsix(string tag) =>
        new($"VardyParty-windows-v{tag}.msix", $"https://example/{tag}.msix");

    private static GitHubReleaseAssetSnapshot LinuxSnap(string arch, string display, string build) =>
        new($"VardyParty-linux-{arch}-v{display}+{build}.snap", $"https://example/{arch}.snap");
}
