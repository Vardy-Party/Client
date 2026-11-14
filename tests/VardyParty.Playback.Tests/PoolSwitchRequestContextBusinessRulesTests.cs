using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Windows build 251, 13 Nov 2026 19:41 (Slovakia v Faroe Islands): picture on techydeals,
/// then Next onto LiveStreams, streamhd247 and sportswatcher. LiveStreams played but was never
/// counted; streamhd247 and sportswatcher were probed Healthy with their own Referer and got
/// 403 in the player, which still sent techydeals' Referer.
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

    private static readonly EnrichedStream Techydeals = Row(
        "techydeals", "https://c2807p.7odxv0l067ka.net:8443/hls/cd4dh6iarhr2p.m3u8?s=x&e=1794620397", "https://lineagest.click/");

    private static readonly PlaybackRequestContext Opening = new("https://lineagest.click/", null);

    [Fact]
    public void Rule_NextAttach_SendsTheTargetRowsReferer_NotTheOpeningStreams()
    {
        // Arrange
        var pool = new StreamSwitchingService();
        pool.Initialize("UEFA Nations League", "Slovakia", "Faroe Islands");
        var streamhd = Row("streamhd247", "https://edge2.candyshop.st/live3/streamhd3/index.m3u8?st=a&e=1794609641", "https://xstream.st/");
        pool.AddHealthyStream(Techydeals);
        pool.AddHealthyStream(streamhd);
        pool.SwitchToNextStream();

        // Act
        var context = PlaybackRowRequestContext.ForUrl(pool, streamhd.ResolvedM3U8Url, Opening);

        // Assert
        Assert.Equal("https://xstream.st/", context.Referer);
    }

    [Fact]
    public void Rule_RevertAttach_GoesBackToTheLastGoodRowsReferer()
    {
        // Arrange - the index is on sportswatcher; the session reverts to techydeals' URL.
        var pool = new StreamSwitchingService();
        pool.Initialize("UEFA Nations League", "Slovakia", "Faroe Islands");
        var sportswatcher = Row("sportswatcher", "https://hls.lshls.st/secure_hls.php?path=a&e=1794600641", "https://matchli.st/");
        pool.AddHealthyStream(Techydeals);
        pool.AddHealthyStream(sportswatcher);
        pool.SwitchToNextStream();

        // Act
        var context = PlaybackRowRequestContext.ForUrl(pool, Techydeals.ResolvedM3U8Url, new("https://matchli.st/", null));

        // Assert
        Assert.Equal("https://lineagest.click/", context.Referer);
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
    public void ForUrl_UrlOfNoPoolRow_KeepsTheCurrentContext()
    {
        // Arrange - e.g. a mirror retry inside the player.
        var pool = new StreamSwitchingService();
        pool.Initialize("UEFA Nations League", "Slovakia", "Faroe Islands");
        pool.AddHealthyStream(Techydeals);

        // Act
        var context = PlaybackRowRequestContext.ForUrl(pool, "https://edge.candyshop.st/live3/streamhd3/index.m3u8", Opening);

        // Assert
        Assert.Same(Opening, context);
    }

    [Fact]
    public async Task Rule_HostWithoutVideoConfirmation_ReadyConfirmsTheSwitch_AndClearsItsTriedMark()
    {
        // Arrange - Windows/Linux/Apple keep video confirmation off: Ready is the picture.
        var pool = new StreamSwitchingService();
        pool.Initialize("UEFA Nations League", "Slovakia", "Faroe Islands");
        var live = Row("LiveStreams", "https://sha.sharpnessonline.store/simpch21/index.m3u8?token=a.b", "https://universaltokenforall.st/");
        pool.AddHealthyStream(Techydeals);
        pool.AddHealthyStream(live);
        var session = new PlaybackSessionController();
        _ = new PlaybackPoolCommandActions(session, pool, null, (_, _, _) => { }, _ => { });
        var confirmed = new List<EnrichedStream?>();
        using var sub = pool.PlayingConfirmed!.Subscribe(new Collect(confirmed));
        session.BeginAttach(Techydeals.ResolvedM3U8Url!, usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        pool.SwitchToNextStream();
        pool.MarkTriedInCycle(live, null, "earlier failure");

        // Act
        session.BeginAttach(live.ResolvedM3U8Url!, usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        await Task.Yield();

        // Assert
        Assert.Equal(new[] { Techydeals, live }, confirmed);
        Assert.False(pool.IsSkippedInCycle(live));
    }

    private sealed class Collect(List<EnrichedStream?> into) : IObserver<EnrichedStream?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(EnrichedStream? value) => into.Add(value);
    }
}