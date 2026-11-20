using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// Android launch surfaces must paint one splash. The system starting window,
/// SplashActivity (which draws nothing over its window) and MainActivity all show
/// <c>@drawable/splash_full</c> as their window background, and the art is sized in
/// that drawable, never scaled in code. A FitCenter ImageView in SplashActivity drew
/// the art about 1.18x larger on a 1080p TV, so the splash zoomed after launch (3.6.2-b273).
/// </summary>
public class AndroidSplashSurfaceParityTests
{
    private const string SplashBackground = "@drawable/splash_full";
    private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    [Theory]
    [InlineData("values", "VardyParty.SplashTheme")]
    [InlineData("values", "VardyParty.MauiTheme")]
    [InlineData("values", "VardyParty.PhoneSplashTheme")]
    [InlineData("values-v31", "VardyParty.SplashTheme")]
    [InlineData("values-v31", "VardyParty.MauiTheme")]
    [InlineData("values-v31", "VardyParty.PhoneSplashTheme")]
    public void LaunchTheme_WindowBackground_IsSplashFull(string valuesFolder, string theme)
    {
        // Arrange
        var styles = XDocument.Load(AndroidResource(valuesFolder, "styles.xml"));

        // Act
        var windowBackground = styles.Descendants("style")
            .Single(s => (string?)s.Attribute("name") == theme)
            .Elements("item")
            .Single(i => (string?)i.Attribute("name") == "android:windowBackground")
            .Value.Trim();

        // Assert
        Assert.Equal(SplashBackground, windowBackground);
    }

    [Fact]
    public void SplashFull_ArtLayer_IsSizedByDimen()
    {
        // Arrange
        var splashFull = XDocument.Load(AndroidResource("drawable", "splash_full.xml"));

        // Act
        var art = splashFull.Root!.Elements("item")
            .Single(i => (string?)i.Attribute(Android + "drawable") == "@drawable/vardyparty_splash_generated");

        // Assert
        Assert.Equal("@dimen/splash_art_size", (string?)art.Attribute(Android + "width"));
        Assert.Equal("@dimen/splash_art_size", (string?)art.Attribute(Android + "height"));
        Assert.Equal("center", (string?)art.Attribute(Android + "gravity"));
    }

    [Theory]
    [InlineData("values-sw480dp", 456)]
    [InlineData("values", 320)]
    public void SplashArtSize_FitsTheScreenClass(string valuesFolder, int expectedDp)
    {
        // Arrange - 456dp is the MauiSplashScreen BaseSize (TV, tablets); narrower
        // phones get a size that fits a 320dp-wide screen instead of cropping.
        var dimens = XDocument.Load(AndroidResource(valuesFolder, "dimens.xml"));

        // Act
        var size = dimens.Descendants("dimen")
            .Single(d => (string?)d.Attribute("name") == "splash_art_size")
            .Value.Trim();

        // Assert
        Assert.Equal($"{expectedDp}dp", size);
    }

    private static string AndroidResource(string folder, string file) =>
        RepoFiles.Path("VardyParty", "Platforms", "Android", "Resources", folder, file);
}