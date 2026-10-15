using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class DesktopUpdateDownloadTests
{
    [Fact]
    public void FileNameFromAsset_PlainName_IsUnchanged()
    {
        // Arrange / Act
        var name = DesktopUpdateDownload.FileNameFromAsset("VardyParty-windows-v2.1.0-b160.msix");
        var snap = DesktopUpdateDownload.FileNameFromAsset("VardyParty-linux-x64-v2.1.2+164.snap");

        // Assert
        Assert.Equal("VardyParty-windows-v2.1.0-b160.msix", name);
        Assert.Equal("VardyParty-linux-x64-v2.1.2+164.snap", snap);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../evil.msix")]
    [InlineData("dir/payload.msix")]
    public void FileNameFromAsset_PathOrEmpty_Throws(string? assetName)
    {
        Assert.Throws<InvalidOperationException>(() => DesktopUpdateDownload.FileNameFromAsset(assetName));
    }

    [Theory]
    [InlineData("https://github.com/example/Client/releases/download/x/a.msix", true)]
    [InlineData("https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-linux-x64-v2.1.2%2B164.snap", true)]
    [InlineData("https://github.com/example/Client/releases/download/2.1.2-b164/VardyParty-linux-x64-v2.1.2%2B164.snap.minisig", true)]
    [InlineData("https://objects.githubusercontent.com/foo", true)]
    [InlineData("https://release-assets.githubusercontent.com/foo", true)]
    [InlineData("http://github.com/foo", false)]
    [InlineData("https://evil.example/a.msix", false)]
    [InlineData("not-a-url", false)]
    public void IsAllowedDownloadUrl_GitHubHttpsOnly(string url, bool expected)
    {
        Assert.Equal(expected, DesktopUpdateDownload.IsAllowedDownloadUrl(url));
    }
}
