using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// The desktop badge only appears for GitHub releases that have been
/// published for at least two full days (so a bad package can be yanked).
/// Ages are spelled as <see cref="TimeSpan"/> from <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public class DesktopUpdateMaturityTests
{
    private static readonly AppReleaseVersion Running = new(2, 0, 0, 159);

    [Fact]
    public void Maturity_IsExactlyTwoDays()
    {
        Assert.Equal(TimeSpan.FromDays(2), DesktopUpdatePolicy.Maturity);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, false)]   // just published
    [InlineData(1, 0, 0, 0, false)]   // 1 day old
    [InlineData(1, 23, 59, 59, false)] // 1 day 23:59:59 — still cooling off
    [InlineData(2, 0, 0, 0, true)]    // exactly 2 days — first moment we offer it
    [InlineData(2, 0, 0, 1, true)]    // 2 days + 1 second
    [InlineData(3, 0, 0, 0, true)]    // 3 days
    public void IsMature_TwoDayCutoff(
        int days,
        int hours,
        int minutes,
        int seconds,
        bool expectedMature)
    {
        // Arrange: publishedAt is this much earlier than now.
        var now = DateTimeOffset.UtcNow;
        var age = new TimeSpan(days, hours, minutes, seconds);
        var publishedAt = now - age;

        // Act
        var mature = DesktopUpdatePolicy.IsMature(publishedAt, now);

        // Assert
        Assert.Equal(expectedMature, mature);
    }

    [Fact]
    public void SelectOffer_OneSecondShyOfTwoDays_IsNotOffered()
    {
        // Arrange: 1 second younger than two days.
        var now = DateTimeOffset.UtcNow;
        var publishedAt = now - TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1);
        var releases = new[]
        {
            Release("2.1.0-b160", publishedAt),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, Running, DesktopUpdatePlatform.Windows, now);

        // Assert
        Assert.True(publishedAt > now - TimeSpan.FromDays(2));
        Assert.Null(offer);
    }

    [Fact]
    public void SelectOffer_PublishedExactlyTwoDaysAgo_IsOffered()
    {
        // Arrange: published exactly 48 hours before now.
        var now = DateTimeOffset.UtcNow;
        var publishedAt = now - TimeSpan.FromDays(2);
        var releases = new[]
        {
            Release("2.1.0-b160", publishedAt),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, Running, DesktopUpdatePlatform.Windows, now);

        // Assert
        Assert.Equal(TimeSpan.FromDays(2), now - publishedAt);
        Assert.NotNull(offer);
        Assert.Equal("2.1.0-b160", offer.Tag);
        Assert.Equal(publishedAt, offer.PublishedAt);
    }

    [Fact]
    public void SelectOffer_YoungerNewerRelease_DoesNotHideMatureOlderUpdate()
    {
        // Arrange: 2.2.0 is newer but only 12 hours old (hidden). 2.1.0 is
        // 3 days old and still newer than the running 2.0.0-b159.
        var now = DateTimeOffset.UtcNow;
        var twelveHoursAgo = now - TimeSpan.FromHours(12);
        var threeDaysAgo = now - TimeSpan.FromDays(3);
        var releases = new[]
        {
            Release("2.2.0-b170", twelveHoursAgo),
            Release("2.1.0-b160", threeDaysAgo),
        };

        // Act
        var offer = DesktopUpdatePolicy.SelectOffer(
            releases, Running, DesktopUpdatePlatform.Windows, now);

        // Assert
        Assert.NotNull(offer);
        Assert.Equal("2.1.0-b160", offer.Tag);
        Assert.Equal(threeDaysAgo, offer.PublishedAt);
    }

    private static GitHubReleaseSnapshot Release(string tag, DateTimeOffset published) =>
        new(
            tag,
            Draft: false,
            Prerelease: false,
            published,
            [new GitHubReleaseAssetSnapshot(
                $"VardyParty-windows-v{tag}.msix",
                $"https://example/{tag}.msix")]);
}
