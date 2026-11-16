using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Windows build 251, 13 Nov 2026 19:41 (Home United v Away City): picture on birchsite,
/// then Next onto PineSite, maplesite and cedarsite. PineSite played but was never
/// counted; maplesite and cedarsite were probed Healthy with their own Referer and got
/// 403 in the player, which still sent birchsite' Referer.
/// </summary>
public class PoolSwitchRequestContextBusinessRulesTests
{
    private static EnrichedStream Row(string channel, string url, string? referer, Dictionary<string, string>? headers = null) => new()
    {
        Stream = new Stream { Url = $"https://{channel}.page.test/watch", Channel = channel },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
        Referer = referer,
        RequestHeaders = headers,
    };

    private static readonly EnrichedStream Birch = Row(
        "birchsite", "https://c2807p.cdn-birch.test:8443/hls/cd4dh6iarhr2p.m3u8?s=x&e=1794620397", "https://birch-player.test/");

    private static readonly PlaybackRequestContext Opening = new("https://birch-player.test/", null);

    [Fact]
    public void Rule_NextAttach_SendsTheTargetRowsReferer_NotTheOpeningStreams()
    {
        // Arrange
        var pool = new StreamSwitchingService();
        pool.Initialize("Fictional League", "Home United", "Away City");
        var maple = Row("maplesite", "https://edge2.cdn-maple.test/live3/maple3/index.m3u8?st=a&e=1794609641", "https://maple-player.test/");
        pool.AddHealthyStream(Birch);
        pool.AddHealthyStream(maple);
        pool.SwitchToNextStream();

        // Act
        var context = PlaybackRowRequestContext.ForUrl(pool, maple.ResolvedM3U8Url, Opening);

        // Assert
        Assert.Equal("https://maple-player.test/", context.Referer);
    }

    [Fact]
    public void Rule_RevertAttach_GoesBackToTheLastGoodRowsReferer()
    {
        // Arrange - the index is on cedarsite; the session reverts to birchsite' URL.
        var pool = new StreamSwitchingService();
        pool.Initialize("Fictional League", "Home United", "Away City");
        var cedarsite = Row("cedarsite", "https://hls.cdn-cedar.test/secure_hls.php?path=a&e=1794600641", "https://cedar-player.test/");
        pool.AddHealthyStream(Birch);
        pool.AddHealthyStream(cedarsite);
        pool.SwitchToNextStream();

        // Act
        var context = PlaybackRowRequestContext.ForUrl(pool, Birch.ResolvedM3U8Url, new("https://cedar-player.test/", null));

        // Assert
        Assert.Equal("https://birch-player.test/", context.Referer);
    }

    [Fact]
    public void ForRow_RowHeaders_ReplaceThePreviousRowsHeaders_AndSupplyTheReferer()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["Referer"] = "https://player.oak.test/", ["Origin"] = "https://player.oak.test" };
        var row = Row("oak", "https://cdn.oak.test/index.m3u8", referer: null, headers);

        // Act
        var context = PlaybackRowRequestContext.ForRow(row);

        // Assert
        Assert.Equal("https://player.oak.test/", context.Referer);
        Assert.Same(headers, context.Headers);
    }

    [Fact]
    public void ForRow_NoRefererAnywhere_UsesTheRowsPage_AndNoHeaders()
    {
        // Arrange - same rule as the first open (Referer, else the row's catalog page).
        var row = Row("elm", "https://cdn.elm.test/index.m3u8", referer: null);

        // Act
        var context = PlaybackRowRequestContext.ForRow(row);

        // Assert
        Assert.Equal("https://elm.page.test/watch", context.Referer);
        Assert.Null(context.Headers);
    }

    [Fact]
    public void ForRow_ChromeErrorReferer_FallsBackToTheRowsPage()
    {
        // Arrange — health/play captured chrome-error:// from a failed scrape frame.
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "chrome-error://chromewebdata/",
            ["Origin"] = "chrome-error://chromewebdata"
        };
        var row = Row("elm", "https://cdn.elm.test/index.m3u8", referer: "chrome-error://chromewebdata/", headers);

        // Act
        var context = PlaybackRowRequestContext.ForRow(row);

        // Assert
        Assert.Equal("https://elm.page.test/watch", context.Referer);
        Assert.Same(headers, context.Headers);
    }

    [Fact]
    public void ForUrl_UrlOfNoPoolRow_KeepsTheCurrentContext()
    {
        // Arrange - e.g. a mirror retry inside the player.
        var pool = new StreamSwitchingService();
        pool.Initialize("Fictional League", "Home United", "Away City");
        pool.AddHealthyStream(Birch);

        // Act
        var context = PlaybackRowRequestContext.ForUrl(pool, "https://edge.cdn-maple.test/live3/maple3/index.m3u8", Opening);

        // Assert
        Assert.Same(Opening, context);
    }

    [Fact]
    public async Task Rule_HostWithoutVideoConfirmation_ReadyConfirmsTheSwitch_AndClearsItsTriedMark()
    {
        // Arrange - Windows/Linux/Apple keep video confirmation off: Ready is the picture.
        var pool = new StreamSwitchingService();
        pool.Initialize("Fictional League", "Home United", "Away City");
        var live = Row("PineSite", "https://sha.cdn-ash.test/simpch21/index.m3u8?token=a.b", "https://ash-player.test/");
        pool.AddHealthyStream(Birch);
        pool.AddHealthyStream(live);
        var session = new PlaybackSessionController();
        _ = new PlaybackPoolCommandActions(session, pool, null, (_, _, _) => { }, _ => { });
        var confirmed = new List<EnrichedStream?>();
        using var sub = pool.PlayingConfirmed!.Subscribe(new Collect(confirmed));
        session.BeginAttach(Birch.ResolvedM3U8Url!, usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        pool.SwitchToNextStream();
        pool.MarkTriedInCycle(live, null, "earlier failure");

        // Act
        session.BeginAttach(live.ResolvedM3U8Url!, usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        await Task.Yield();

        // Assert
        Assert.Equal(new[] { Birch, live }, confirmed);
        Assert.False(pool.IsSkippedInCycle(live));
    }

    private sealed class Collect(List<EnrichedStream?> into) : IObserver<EnrichedStream?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(EnrichedStream? value) => into.Add(value);
    }
}