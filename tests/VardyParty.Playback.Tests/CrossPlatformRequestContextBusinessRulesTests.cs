using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Round 13 (Jon, 13 Nov 2026): Android and Linux follow the Windows rules from build 251.
/// Every switch, revert and retry sends the target row's own headers, never the previous
/// stream's. Missing headers (browser UA, Referer and Origin from the row) are filled in by
/// the shared rule. Refused downloads quick-fail on Android ("Stream refused (403) - trying
/// the next stream"), and Android's "video shown" signal confirms the switch.
/// </summary>
public class CrossPlatformRequestContextBusinessRulesTests
{
    private static EnrichedStream Row(string channel, string url, string? referer, Dictionary<string, string>? headers = null) => new()
    {
        Stream = new Stream { Url = $"https://{channel}.page.test/watch", Channel = channel },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
        Referer = referer,
        RequestHeaders = headers,
    };

    private static readonly Dictionary<string, string> CookieRowHeaders = new()
    {
        ["Referer"] = "https://player.oak.test/",
        ["Cookie"] = "sess=oak",
    };

    private static StreamSwitchingService Pool(params EnrichedStream[] rows)
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("Copa del Rey", "Real Betis", "Sevilla");
        foreach (var row in rows)
            pool.AddHealthyStream(row);
        return pool;
    }

    [Fact]
    public void Rule_SwitchToARowWithoutHeaders_SendsNoneOfThePreviousRowsHeaders()
    {
        // Arrange - playing oak (Cookie + Referer); Next onto elm, which has a Referer only.
        var oak = Row("oak", "https://cdn.oak.test/index.m3u8", referer: null, CookieRowHeaders);
        var elm = Row("elm", "https://cdn.elm.test/index.m3u8", "https://embed.elm.test/");
        var pool = Pool(oak, elm);
        pool.SwitchToNextStream();
        var playing = PlaybackRowRequestContext.ForRow(oak);

        // Act
        var headers = PlaybackRequestHeaders.ForContext(PlaybackRowRequestContext.ForUrl(pool, elm.ResolvedM3U8Url, playing));

        // Assert
        Assert.False(headers.ContainsKey("Cookie"));
        Assert.Equal("https://embed.elm.test/", headers["Referer"]);
        Assert.Equal("https://embed.elm.test", headers["Origin"]);
    }

    [Fact]
    public void Rule_SwitchToARowWithoutAnyReferer_UsesItsOwnPage_NotThePreviousReferer()
    {
        // Arrange
        var oak = Row("oak", "https://cdn.oak.test/index.m3u8", "https://embed.oak.test/");
        var ash = Row("ash", "https://cdn.ash.test/index.m3u8", referer: null);
        var pool = Pool(oak, ash);
        pool.SwitchToNextStream();

        // Act
        var context = PlaybackRowRequestContext.ForUrl(pool, ash.ResolvedM3U8Url, PlaybackRowRequestContext.ForRow(oak));

        // Assert
        Assert.Equal("https://ash.page.test/watch", context.Referer);
        Assert.Null(context.Headers);
    }

    [Fact]
    public void Rule_RevertToLastGood_SendsTheLastGoodRowsHeaders_NotTheFailedRows()
    {
        // Arrange - the index is on the failed elm; the session reverts to oak's URL.
        var oak = Row("oak", "https://cdn.oak.test/index.m3u8", referer: null, CookieRowHeaders);
        var elm = Row("elm", "https://cdn.elm.test/index.m3u8", "https://embed.elm.test/");
        var pool = Pool(oak, elm);
        pool.SwitchToNextStream();

        // Act
        var headers = PlaybackRequestHeaders.ForContext(
            PlaybackRowRequestContext.ForUrl(pool, oak.ResolvedM3U8Url, PlaybackRowRequestContext.ForRow(elm)));

        // Assert
        Assert.Equal("sess=oak", headers["Cookie"]);
        Assert.Equal("https://player.oak.test/", headers["Referer"]);
    }

    [Fact]
    public void Rule_RowHeadersWithoutUa_GetTheBrowserUa_AndKeepTheirOwnReferer()
    {
        // Arrange
        var context = PlaybackRowRequestContext.ForRow(Row("oak", "https://cdn.oak.test/index.m3u8", referer: null, CookieRowHeaders));

        // Act
        var headers = PlaybackRequestHeaders.ForContext(context);

        // Assert
        Assert.Equal(PlaybackRequestHeaders.BrowserUserAgent, headers["User-Agent"]);
        Assert.Equal("https://player.oak.test/", headers["Referer"]);
        Assert.Equal("sess=oak", headers["Cookie"]);
        // Origin is derived only when the row carries no Referer of its own (same as Windows).
        Assert.False(headers.ContainsKey("Origin"));
    }

    [Fact]
    public void Rule_RowsOwnUserAgent_IsKept_OverTheFilledInOne()
    {
        // Arrange
        var rowHeaders = new Dictionary<string, string> { ["user-agent"] = "Harvest/1.0" };

        // Act
        var headers = PlaybackRequestHeaders.ForContext(new PlaybackRequestContext("https://embed.oak.test/", rowHeaders), "Linux UA");

        // Assert
        Assert.Equal("Harvest/1.0", headers["User-Agent"]);
        Assert.Single(headers, h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rule_PlatformBrowserUserAgent_IsUsedWhenTheRowHasNone()
    {
        // Act
        var headers = PlaybackRequestHeaders.ForContext(new PlaybackRequestContext("https://embed.oak.test/", null), "Linux UA");

        // Assert
        Assert.Equal("Linux UA", headers["User-Agent"]);
    }

    [Theory]
    [InlineData("HTTP 403", 403)]
    [InlineData("java.io.IOException: HTTP 401", 401)]
    [InlineData("Response code: 403", 403)]
    [InlineData("Segment download failed (MediaSegment, status=403)", 403)]
    [InlineData("HTTP/1.1 403 Forbidden", 403)]
    [InlineData("Unable to connect to cdn.oak.test", 0)]
    [InlineData("HTTP 9999", 0)]
    [InlineData(null, 0)]
    public void HttpStatusFromErrorText_ReadsTheStatusOfAFailedDownload(string? text, int expected)
    {
        // Act
        var status = PlaybackPolicy.HttpStatusFromErrorText(text);

        // Assert
        Assert.Equal(expected, status);
    }

    [Fact]
    public void Rule_AndroidSession_TwoRefusedDownloadsOnASwitch_FailIt_WithTheStreamRefusedText()
    {
        // Arrange - Android keeps video confirmation on; oak showed a picture, then Next onto elm.
        var session = new PlaybackSessionController();
        session.EnableVideoConfirmation();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("https://cdn.oak.test/index.m3u8", usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.Handle(MediaEngineEvent.VideoProbe(session.Snapshot.AttachGeneration, true));
        session.BeginAttach("https://cdn.elm.test/index.m3u8");

        // Act
        var first = session.NotifyDownloadFailure("Segment download failed (status=403)", 403);
        var second = session.NotifyDownloadFailure("Segment download failed (status=403)", 403);

        // Assert
        Assert.All(first, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        var command = PlaybackCommand.FromEffects(second);
        Assert.False(command.IsNoOp);
        Assert.StartsWith("Stream refused (403)", command.UserFacingFailureText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rule_AndroidSwitch_IsConfirmedWhenThePictureShows_NotOnReady()
    {
        // Arrange
        var oak = Row("oak", "https://cdn.oak.test/index.m3u8", "https://embed.oak.test/");
        var elm = Row("elm", "https://cdn.elm.test/index.m3u8", "https://embed.elm.test/");
        var pool = Pool(oak, elm);
        var session = new PlaybackSessionController();
        session.EnableVideoConfirmation();
        _ = new PlaybackPoolCommandActions(session, pool, null, (_, _, _) => { }, _ => { });
        var confirmed = new List<EnrichedStream?>();
        using var sub = pool.PlayingConfirmed!.Subscribe(new Collect(confirmed));
        session.BeginAttach(oak.ResolvedM3U8Url!, usedCachedUrl: true);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.Handle(MediaEngineEvent.VideoProbe(session.Snapshot.AttachGeneration, true));
        pool.SwitchToNextStream();
        session.BeginAttach(elm.ResolvedM3U8Url!, usedCachedUrl: true);

        // Act
        var onReady = session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        var confirmedAfterReady = session.Snapshot.IsVideoConfirmed;
        var onPicture = session.Handle(MediaEngineEvent.VideoProbe(session.Snapshot.AttachGeneration, true));
        await Task.Yield();

        // Assert
        Assert.DoesNotContain(onReady, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.False(confirmedAfterReady);
        Assert.Contains(onPicture, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.Equal(new[] { oak, elm }, confirmed);
    }

    [Fact]
    public void Rule_AndroidPictureBeforeReady_ConfirmsTheSwitchAtReady()
    {
        // Arrange - the first frame can arrive before ExoPlayer reports READY.
        var session = new PlaybackSessionController();
        session.EnableVideoConfirmation();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("https://cdn.oak.test/index.m3u8", usedCachedUrl: true);
        session.Handle(MediaEngineEvent.VideoProbe(session.Snapshot.AttachGeneration, true));

        // Act
        var onReady = session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Assert
        Assert.Contains(onReady, e => e.Kind == PlaybackEffectKind.ReportWorking);
        Assert.True(session.Snapshot.IsVideoConfirmed);
    }

    [Theory]
    [InlineData("https://cdn.elm.test/index.m3u8", "https://cdn.elm.test/index.m3u8", true)]
    [InlineData("https://cdn.oak.test/index.m3u8", "https://cdn.elm.test/index.m3u8", false)]
    [InlineData(null, "https://cdn.elm.test/index.m3u8", false)]
    [InlineData("https://cdn.elm.test/index.m3u8", null, false)]
    public void Rule_APictureCountsOnlyForTheAttachThePlayerWasGiven(string? playerUrl, string? sessionUrl, bool counts)
    {
        // Act
        var result = PlaybackPolicy.IsPictureForCurrentAttach(playerUrl, sessionUrl);

        // Assert - the old stream's frame between BeginAttach and the engine attach must not count.
        Assert.Equal(counts, result);
    }

    private sealed class Collect(List<EnrichedStream?> into) : IObserver<EnrichedStream?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(EnrichedStream? value) => into.Add(value);
    }
}