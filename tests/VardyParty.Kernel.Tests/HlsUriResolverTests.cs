using System;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

/// <summary>
/// HLS playlist URI resolution must be identical on Android, Linux and Windows.
/// On Unix-like runtimes <c>Uri.TryCreate("/seg.ts", UriKind.Absolute, ...)</c> succeeds
/// as <c>file:///seg.ts</c>; that made Android health checks fetch an invalid request
/// URI and report live root-relative streams as SegmentUnreachable. These tests pin
/// the resolver independent of the host OS.
/// </summary>
public class HlsUriResolverTests
{
    private const string Playlist = "https://host/a/b/index.m3u8";

    [Theory]
    [InlineData("/seg.ts", "https://host/seg.ts")]
    [InlineData("seg.ts", "https://host/a/b/seg.ts")]
    [InlineData("../x.ts", "https://host/a/x.ts")]
    [InlineData("//cdn.example/x.ts", "https://cdn.example/x.ts")]
    [InlineData("/hls-segments/abc123/6195.ts?e=1793478144&s=Zx9", "https://host/hls-segments/abc123/6195.ts?e=1793478144&s=Zx9")]
    [InlineData("seg.ts?token=abc&e=5", "https://host/a/b/seg.ts?token=abc&e=5")]
    [InlineData("  /seg.ts  ", "https://host/seg.ts")]
    public void TryResolve_CombinesReferencesWithThePlaylistUrl(string reference, string expected)
    {
        // Arrange — reference / expected from InlineData

        // Act
        var ok = HlsUriResolver.TryResolve(Playlist, reference, out string? resolved);

        // Assert
        Assert.True(ok);
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("https://other.example/live/seg.ts?token=a%2Fb&e=1")]
    [InlineData("http://other.example/seg.ts")]
    public void TryResolve_AbsoluteHttpUrl_IsReturnedUnchanged(string reference)
    {
        // Arrange — reference from InlineData

        // Act
        var ok = HlsUriResolver.TryResolve(Playlist, reference, out string? resolved);

        // Assert
        Assert.True(ok);
        Assert.Equal(reference, resolved);
    }

    [Fact]
    public void TryResolve_ProtocolRelative_KeepsThePlaylistScheme()
    {
        // Arrange
        const string httpPlaylist = "http://host/a/index.m3u8";
        const string reference = "//cdn.example/x.ts";

        // Act
        var ok = HlsUriResolver.TryResolve(httpPlaylist, reference, out string? resolved);

        // Assert
        Assert.True(ok);
        Assert.Equal("http://cdn.example/x.ts", resolved);
    }

    [Theory]
    [InlineData("file:///seg.ts")]
    [InlineData("data:text/plain,abc")]
    [InlineData("skd://key-id")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_NonHttpReference_IsNotAFetchableUrl(string reference)
    {
        // Arrange — reference from InlineData

        // Act
        var ok = HlsUriResolver.TryResolve(Playlist, reference, out string? resolved);

        // Assert
        Assert.False(ok);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_NonHttpPlaylistBase_CannotResolveRelativeReferences()
    {
        // Arrange
        var baseUri = new Uri("file:///a/b/index.m3u8");

        // Act
        var ok = HlsUriResolver.TryResolve(baseUri, "/seg.ts", out Uri? resolved);

        // Assert
        Assert.False(ok);
        Assert.Null(resolved);
    }

    [Fact]
    public void IsHttpUrl_RejectsAFileUri_AsUnixParsesARootRelativePath()
    {
        // Arrange — what Uri.TryCreate("/seg.ts", UriKind.Absolute) yields on Android/Linux.
        var unixParse = new Uri("file:///seg.ts");

        // Act
        var absolute = unixParse.IsAbsoluteUri;
        var httpFromUri = HlsUriResolver.IsHttpUrl(unixParse);
        var httpFromString = HlsUriResolver.IsHttpUrl("file:///seg.ts");
        var tryGet = HlsUriResolver.TryGetHttpUrl("/seg.ts", out _);

        // Assert
        Assert.True(absolute);
        Assert.False(httpFromUri);
        Assert.False(httpFromString);
        Assert.False(tryGet);
    }

    [Theory]
    [InlineData("/seg.ts")]
    [InlineData("/hls-segments/abc/1.ts?e=1")]
    [InlineData("seg.ts")]
    public void TryCreateExplicitAbsolute_PathsAreNeverAbsoluteOnAnyPlatform(string value)
    {
        // Arrange — value from InlineData

        // Act
        var ok = HlsUriResolver.TryCreateExplicitAbsolute(value, out _);

        // Assert
        Assert.False(ok);
    }

    [Theory]
    [InlineData("https://host/x.m3u8")]
    [InlineData("HTTPS://HOST/x.m3u8")]
    [InlineData("file:///seg.ts")]
    public void TryCreateExplicitAbsolute_AcceptsStringsWithTheirOwnScheme(string value)
    {
        // Arrange — value from InlineData

        // Act
        var ok = HlsUriResolver.TryCreateExplicitAbsolute(value, out var uri);

        // Assert
        Assert.True(ok);
        Assert.NotNull(uri);
        Assert.True(uri!.IsAbsoluteUri);
    }

    [Theory]
    [InlineData("https://host/a.m3u8", true)]
    [InlineData("http://host/a.m3u8", true)]
    [InlineData("file:///a.m3u8", false)]
    [InlineData("/a.m3u8", false)]
    [InlineData("ftp://host/a.m3u8", false)]
    [InlineData(null, false)]
    public void IsHttpUrl_OnlyExplicitHttpOrHttps(string? value, bool expected)
    {
        // Arrange — value from InlineData

        // Act
        var isHttp = HlsUriResolver.IsHttpUrl(value);

        // Assert
        Assert.Equal(expected, isHttp);
    }

    [Fact]
    public void Resolve_FallsBackToTheTrimmedReference_WhenUnresolvable()
    {
        // Arrange
        const string skd = " skd://key-id ";
        const string rootRelative = "/seg.ts";

        // Act
        var skdResolved = HlsUriResolver.Resolve(Playlist, skd);
        var rootResolved = HlsUriResolver.Resolve(Playlist, rootRelative);

        // Assert
        Assert.Equal("skd://key-id", skdResolved);
        Assert.Equal("https://host/seg.ts", rootResolved);
    }

    [Fact]
    public void NormalizeForDedup_RootRelativePath_IsSameOnEveryPlatform()
    {
        // Arrange
        const string path = "/live/x.m3u8?t=1";

        // Act
        var normalized = StreamUrlNormalizer.NormalizeForDedup(path);

        // Assert — never "file:///live/x.m3u8" on Unix.
        Assert.Equal("/live/x.m3u8", normalized);
    }
}
