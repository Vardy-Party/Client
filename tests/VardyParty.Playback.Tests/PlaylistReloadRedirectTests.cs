using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaylistReloadRedirectTests
{
    private const string Playing = "https://edge.example.test/premium38/index.m3u8?t=100.abc&_=1";
    private const string Next = "https://edge.example.test/premium38/index.m3u8?t=200.def&_=2";

    [Fact]
    public void Rewrite_SamePlaylistWithANewToken_ReturnsTheNewUrl()
    {
        // Arrange
        var request = Playing;

        // Act
        var rewritten = PlaylistReloadRedirect.Rewrite(request, Playing, Next);

        // Assert
        Assert.Equal(Next, rewritten);
    }

    [Fact]
    public void Rewrite_SegmentUrl_IsLeftAlone()
    {
        // Arrange
        var segment = "https://cdn.example.test/tiktok-obj/segment.image?x-expires=2108919600";

        // Act
        var rewritten = PlaylistReloadRedirect.Rewrite(segment, Playing, Next);

        // Assert
        Assert.Null(rewritten);
    }

    [Fact]
    public void Rewrite_DifferentPlaylist_IsLeftAlone()
    {
        // Arrange
        var other = "https://edge.example.test/premium15/index.m3u8?t=200.def&_=2";

        // Act
        var rewritten = PlaylistReloadRedirect.Rewrite(other, Playing, Next);

        // Assert
        Assert.Null(rewritten);
    }

    [Fact]
    public void Rewrite_RequestAlreadyOnTheNewToken_IsLeftAlone()
    {
        // Arrange
        var request = Next;

        // Act
        var rewritten = PlaylistReloadRedirect.Rewrite(request, Playing, Next);

        // Assert
        Assert.Null(rewritten);
    }
}
